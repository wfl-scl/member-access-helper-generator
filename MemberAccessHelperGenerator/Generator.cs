using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MemberAccessHelperGenerator;

internal static class Generator {

	private const string defaultOutputFolder = @".\Generated";
	private const string instancePropertyName = "InstanceForHelper";

	private const BindingFlags generateTargetBindingFlags =
		BindingFlags.Public |
		BindingFlags.NonPublic |
		BindingFlags.Static |
		BindingFlags.Instance;

	private enum PropertyMethodType {
		Get,
		Set
	}

	private static readonly Type[] excludeTypes = [
		typeof(Delegate),
		typeof(Attribute)
	];


	public static void Run(
		[Option("i", "Assembly file(s) or containing folder(s) to generate")] string input,
		[Option("o", "Output folder", DefaultValue = defaultOutputFolder)] string? output = null,
		[Option("f", "Regex pattern for filter types")] string? filter = null
	) {
		Regex? filterRegex = (filter != null) ? new(filter) : null;

		var types = input
			.Split(',')
			.SelectMany(path => {
				var attribute = File.GetAttributes(path);
				if (attribute.HasFlag(FileAttributes.Directory)) {
					return Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories);
				} else {
					return new[] { path };
				}
			})
			.Select(Assembly.LoadFrom)
			.SelectMany(assembly => {
				Type[] types;
				try {
					types = assembly.GetTypes();
				} catch (ReflectionTypeLoadException ex) {
					types = ex.Types.OfType<Type>().ToArray();
				}
				return types;
			})
			// 自動生成対象外の型を除く
			.Where(type =>
				// struct
				!type.IsValueType &&
				// abstract class (static classもIsAbstractがtrueなので注意)
				(!type.IsAbstract || type.IsStatic()) &&
				// interface
				!type.IsInterface &&
				// コンパイラが生成した型
				!type.IsCompilerGenerated() &&
				// 除外指定された型とそれから派生した型
				!excludeTypes.Any(excludeType => excludeType.IsAssignableFrom(type))
			)
			// 正規表現によるフィルター
			.Where(type => filterRegex?.Match(type.FullName ?? type.Name).Success != false);

		output ??= defaultOutputFolder;

		foreach (var type in types) {
			generateSource(type, output);
		}
	}

	private static bool generateSource(Type type, string outputFolder) {
		if (!type.IsVisible || type.IsGenericType) {
			// 型自体がpublicじゃないやつ、genericはまだ対応してない
			Console.WriteLine($"Cannot generate source for '{type.FullName}'");
			return false;
		}

		IEnumerable<T> filterMembers<T>(IEnumerable<T> members) where T : MemberInfo {
			return members.Where(member => {
				if (member.IsCompilerGenerated()) {
					// コンパイラが生成したメンバーは含めない
					return false;
				}
				if (member.DeclaringType?.Assembly != type.Assembly) {
					// 別のアセンブリから継承したメンバーは含めない
					return false;
				}
				return true;
			});
		}

		string reflectionMembers = string.Empty;
		void addReflectionMemberIfNeeded(string? declaration) {
			if (declaration == null) {
				return;
			}
			if (!string.IsNullOrEmpty(reflectionMembers)) {
				reflectionMembers += "\n";
			}
			reflectionMembers += declaration;
		}

		var explicitInterfaceMethods = type
			.GetInterfaces()
			.Select(type.GetInterfaceMap)
			.SelectMany(map => map.TargetMethods)
			.Where(method => method.IsPrivate)
			.ToArray();

		var properties = string.Empty;
		foreach (var property in filterMembers(type.GetProperties(generateTargetBindingFlags))) {
			if (
				// 明示的に実装されたインターフェースのプロパティは含めない
				explicitInterfaceMethods.Contains(property.GetMethod ?? property.SetMethod) ||
				// インデクサーは含めない
				property.GetIndexParameters().Length > 0
			) {
				continue;
			}
			var declaration = toPropertyDeclaration(property, out var reflectionMemberDeclaration);
			if (!string.IsNullOrEmpty(properties)) {
				properties += "\n";
			}
			properties += declaration;
			addReflectionMemberIfNeeded(reflectionMemberDeclaration);
		}

		var fields = string.Empty;
		foreach (var field in filterMembers(type.GetFields(generateTargetBindingFlags))) {
			if (field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)) {
				continue;
			}
			var declaration = toFieldDeclaration(field, out var reflectionMemberDeclaration);
			if (!string.IsNullOrEmpty(fields)) {
				fields += "\n";
			}
			fields += declaration;
			addReflectionMemberIfNeeded(reflectionMemberDeclaration);
		}

		var methods = string.Empty;
		var methodInfo = filterMembers(type.GetMethods(generateTargetBindingFlags)).ToArray();
		foreach (var method in methodInfo) {
			if (
				// get/setメソッドなどは含めない
				method.IsSpecialName ||
				// 明示的に実装されたインターフェースのメソッドは含めない
				explicitInterfaceMethods.Contains(method)
			) {
				continue;
			}
			// シグネチャが一致するメソッドがあれば new 修飾子をつけているとみなして、
			// 一番派生してる側のメソッドだけを含めるようにする
			// (もっと良いやり方ありそう)
			if (
				methodInfo.Any(x =>
					x != method &&
					x.IsSameSignature(method) &&
					x.DeclaringType?.IsAssignableTo(method.DeclaringType) == true
				)
			) {
				continue;
			}

			// オーバーロードがある場合はReflectionMembersの名前の後ろに数字をつける
			var overloads = methodInfo
				.Where(x =>
					x == method ||
					(x.Name == method.Name && !x.IsSameSignature(method))
				)
				.ToArray();

			var overloadIndex = (overloads.Length >= 2) ?
				Array.IndexOf(overloads, method) :
				-1;

			var declaration = toMethodDeclaration(method, overloadIndex, out var reflectionMemberDeclaration);
			if (!string.IsNullOrEmpty(methods)) {
				methods += "\n";
			}
			methods += declaration;
			addReflectionMemberIfNeeded(reflectionMemberDeclaration);
		}

		var namespaceLines = string.IsNullOrEmpty(type.Namespace) ?
			string.Empty :
			$"\nnamespace {type.Namespace};\n";

		var typeFullName = type.GetTypeName();
		var helperClassName = $"{typeFullName}Helper";
		if (type.Namespace != null) {
			helperClassName = helperClassName.Replace($"{type.Namespace}.", string.Empty);
		}
		helperClassName = helperClassName.Replace(".", "_").Replace("<", "_").Replace(">", "_").Replace(", ", "_");

		var source = $$"""
			// <auto-generated/>
			#pragma warning disable CS0618 // 'member' is obsolete
			{{namespaceLines}}
			public {{(type.IsStatic() ? "static " : string.Empty)}}class {{helperClassName}} {


			""";

		if (!type.IsStatic()) {
			source += $$"""
					public {{typeFullName}} {{instancePropertyName}} { get; set; }


				""";
		}

		if (!string.IsNullOrEmpty(properties)) {
			source += $"{properties}\n";
		}
		if (!string.IsNullOrEmpty(fields)) {
			source += $"{fields}\n";
		}

		if (!type.IsStatic()) {
			source += $$"""
					public {{helperClassName}}(
						{{typeFullName}} instance
					) {
						{{instancePropertyName}} = instance;
					}


				""";
		}

		if (!string.IsNullOrEmpty(methods)) {
			source += $"{methods}\n";
		}
		if (!string.IsNullOrEmpty(reflectionMembers)) {
			source += $$"""
					public static class ReflectionMembers {

						public static T CreateGetFieldMethod<T>(
							{{typeof(FieldInfo).FullName}} field
						) where T : {{typeof(Delegate).FullName}} {
							{{typeof(DynamicMethod).FullName}} method = new(
								name: $"{field.{{nameof(FieldInfo.Name)}}}_Get",
								returnType: field.{{nameof(FieldInfo.FieldType)}},
								parameterTypes: field.{{nameof(FieldInfo.IsStatic)}} ?
									null :
									[typeof({{typeFullName}})],
								restrictedSkipVisibility: true
							);
							var generator = method.{{nameof(DynamicMethod.GetILGenerator)}}();
							if (field.{{nameof(FieldInfo.IsStatic)}}) {
								generator.{{nameof(ILGenerator.Emit)}}({{typeof(OpCodes).FullName}}.{{nameof(OpCodes.Ldsfld)}}, field);
							} else {
								generator.{{nameof(ILGenerator.Emit)}}({{typeof(OpCodes).FullName}}.{{nameof(OpCodes.Ldarg_0)}});
								generator.{{nameof(ILGenerator.Emit)}}({{typeof(OpCodes).FullName}}.{{nameof(OpCodes.Ldfld)}}, field);
							}
							generator.{{nameof(ILGenerator.Emit)}}({{typeof(OpCodes).FullName}}.{{nameof(OpCodes.Ret)}});
							return (T)method.{{nameof(DynamicMethod.CreateDelegate)}}(typeof(T));
						}

						public static T CreateSetFieldMethod<T>(
							{{typeof(FieldInfo).FullName}} field
						) where T : {{typeof(Delegate).FullName}} {
							{{typeof(DynamicMethod).FullName}} method = new(
								name: $"{field.{{nameof(FieldInfo.Name)}}}_Set",
								returnType: null,
								parameterTypes: field.{{nameof(FieldInfo.IsStatic)}} ?
									[field.{{nameof(FieldInfo.FieldType)}}] :
									[typeof({{typeFullName}}), field.{{nameof(FieldInfo.FieldType)}}],
								restrictedSkipVisibility: true
							);
							var generator = method.{{nameof(DynamicMethod.GetILGenerator)}}();
							if (field.{{nameof(FieldInfo.IsStatic)}}) {
								generator.{{nameof(ILGenerator.Emit)}}({{typeof(OpCodes).FullName}}.{{nameof(OpCodes.Ldarg_0)}});
								generator.{{nameof(ILGenerator.Emit)}}({{typeof(OpCodes).FullName}}.{{nameof(OpCodes.Stsfld)}}, field);
							} else {
								generator.{{nameof(ILGenerator.Emit)}}({{typeof(OpCodes).FullName}}.{{nameof(OpCodes.Ldarg_0)}});
								generator.{{nameof(ILGenerator.Emit)}}({{typeof(OpCodes).FullName}}.{{nameof(OpCodes.Ldarg_1)}});
								generator.{{nameof(ILGenerator.Emit)}}({{typeof(OpCodes).FullName}}.{{nameof(OpCodes.Stfld)}}, field);
							}
							generator.{{nameof(ILGenerator.Emit)}}({{typeof(OpCodes).FullName}}.{{nameof(OpCodes.Ret)}});
							return (T)method.{{nameof(DynamicMethod.CreateDelegate)}}(typeof(T));
						}


				{{reflectionMembers}}
					}


				""";
		}

		source += "}\n";

		// CRLFにする
		source = source.ReplaceLineEndings("\r\n");

		if (!Directory.Exists(outputFolder)) {
			Directory.CreateDirectory(outputFolder);
		}
		var sourcePath = Path.Combine(
			outputFolder,
			$"{helperClassName}.cs"
		);
		File.WriteAllText(sourcePath, source);

		return true;
	}

	private static string toPropertyDeclaration(
		PropertyInfo property,
		out string? reflectionMemberDeclaration
	) {
		var isStatic = property.IsStatic();
		var propertyType = property.PropertyType.GetReturnTypeString(property.IsReadOnly());
		var declaration = $"\tpublic {(isStatic ? "static " : string.Empty)}{propertyType} {property.Name} {{\n";

		Type declaringType = property.DeclaringType!;
		var declaringTypeName = declaringType.GetTypeName();

		reflectionMemberDeclaration = null;

		void addReflectionMember(ref string? declaration, PropertyMethodType methodType, out string? methodName) {
			declaration ??= $$"""
						public static {{typeof(PropertyInfo).FullName}} {{property.Name}} { get; } =
							typeof({{declaringTypeName}}).{{nameof(Type.GetProperty)}}(
								"{{property.Name}}",
								{{getBindingFlagsString(property.IsPublic(), isStatic)}}
							);

				""";

			if (!property.PropertyType.IsVisible) {
				// 不明な型の場合はデリゲート作れない？
				methodName = null;
				return;
			}

			methodName = $"{property.Name}_{methodType}";
			var delegateName = $"{methodName}_Delegate";
			switch (methodType) {
				case PropertyMethodType.Get:
					declaration += $$"""

								public delegate {{propertyType}} {{delegateName}}({{(isStatic ? string.Empty : $"{declaringTypeName} instance")}});

								public static {{delegateName}} {{methodName}} { get; } =
									({{delegateName}}){{property.Name}}.{{nameof(PropertyInfo.GetMethod)}}.{{nameof(MethodInfo.CreateDelegate)}}(typeof({{delegateName}}));

						""";
					break;

				case PropertyMethodType.Set:
					declaration += $$"""

								public delegate void {{delegateName}}({{(isStatic ? string.Empty : $"{declaringTypeName} instance, ")}}{{propertyType}} value);

								public static {{delegateName}} {{methodName}} { get; } =
									({{delegateName}}){{property.Name}}.{{nameof(PropertyInfo.SetMethod)}}.{{nameof(MethodInfo.CreateDelegate)}}(typeof({{delegateName}}));

						""";
					break;

				default:
					break;
			}
		}

		if (property.CanRead) {
			string getter;
			if (property.GetMethod!.IsPublic) {
				var prefix = getMemberAccessPrefix(declaringType, isStatic);
				getter = $"{(property.PropertyType.IsByRef ? "ref " : string.Empty)}{prefix}.{property.Name}";
			} else {
				addReflectionMember(ref reflectionMemberDeclaration, PropertyMethodType.Get, out var methodName);
				if (methodName != null) {
					getter = string.Format(
						"{0}ReflectionMembers.{1}({2})",
						property.PropertyType.IsByRef ? "ref " : string.Empty,
						methodName,
						isStatic ? string.Empty : instancePropertyName
					);
				} else {
					getter = string.Format(
						"{0}ReflectionMembers.{1}.{2}({3})",
						property.PropertyType.GetCastString(),
						property.Name,
						nameof(PropertyInfo.GetValue),
						isStatic ? "null" : instancePropertyName
					);
				}
			}
			declaration += $"\t\tget => {getter};\n";
		}

		if (property.CanWrite) {
			string setter;
			if (property.SetMethod!.IsPublic) {
				var prefix = getMemberAccessPrefix(declaringType, isStatic);
				setter = $"{prefix}.{property.Name} = value";
			} else {
				addReflectionMember(ref reflectionMemberDeclaration, PropertyMethodType.Set, out var methodName);
				if (methodName != null) {
					setter = string.Format(
						"ReflectionMembers.{0}({1})",
						methodName,
						isStatic ? "value" : $"{instancePropertyName}, value"
					);
				} else {
					setter = string.Format(
						"ReflectionMembers.{0}.{1}({2}, value)",
						property.Name,
						nameof(PropertyInfo.SetValue),
						isStatic ? "null" : instancePropertyName
					);
				}
			}
			declaration += $"\t\tset => {setter};\n";
		}

		declaration += "\t}\n";
		return declaration;
	}

	private static string toFieldDeclaration(
		FieldInfo field,
		out string? reflectionMemberDeclaration
	) {
		var typeName = field.FieldType.GetTypeName();
		var declaringType = field.DeclaringType!;

		string? getter, setter;
		if (field.IsPublic) {
			var prefix = getMemberAccessPrefix(declaringType, field.IsStatic);
			getter = $"{prefix}.{field.Name}";
			setter = $"{prefix}.{field.Name} = value";

			reflectionMemberDeclaration = null;
		} else {
			reflectionMemberDeclaration = $$"""
						public static {{typeof(FieldInfo).FullName}} {{field.Name}} { get; } =
							typeof({{declaringType.GetTypeName()}}).{{nameof(Type.GetField)}}(
								"{{field.Name}}",
								{{getBindingFlagsString(field.IsPublic, field.IsStatic)}}
							);

				""";

			if (field.IsLiteral) {
				// const
				getter = $"{field.FieldType.GetCastString()}ReflectionMembers.{field.Name}.{nameof(PropertyInfo.GetRawConstantValue)}()";
				setter = null;
			} else if (field.FieldType.IsVisible) {
				// Delegate
				var getDelegateType = field.IsStatic ?
					typeof(Func<>).MakeGenericType(field.FieldType).GetTypeName() :
					typeof(Func<,>).MakeGenericType(declaringType, field.FieldType).GetTypeName();

				reflectionMemberDeclaration += $$"""

							public static {{getDelegateType}} {{field.Name}}_Get { get; } =
								CreateGetFieldMethod<{{getDelegateType}}>({{field.Name}});

					""";

				var instance = field.IsStatic ? string.Empty : instancePropertyName;
				getter = $"ReflectionMembers.{field.Name}_Get({(field.IsStatic ? string.Empty : instancePropertyName)})";

				if (!field.IsInitOnly) {
					var setDelegateType = field.IsStatic ?
						typeof(Action<>).MakeGenericType(field.FieldType).GetTypeName() :
						typeof(Action<,>).MakeGenericType(declaringType, field.FieldType).GetTypeName();

					reflectionMemberDeclaration += $$"""

								public static {{setDelegateType}} {{field.Name}}_Set { get; } =
									CreateSetFieldMethod<{{setDelegateType}}>({{field.Name}});

						""";

					setter = $"ReflectionMembers.{field.Name}_Set({(field.IsStatic ? "value" : $"{instancePropertyName}, value")})";
				} else {
					setter = null;
				}
			} else {
				// Reflection
				var instance = field.IsStatic ? "null" : instancePropertyName;
				getter = $"{field.FieldType.GetCastString()}ReflectionMembers.{field.Name}.{nameof(PropertyInfo.GetValue)}({instance})";
				setter = $"ReflectionMembers.{field.Name}.{nameof(PropertyInfo.SetValue)}({instance}, value)";
			}
		}

		var declaration = $$"""
				public {{(field.IsStatic ? "static " : string.Empty)}}{{typeName}} {{field.Name}} {
					get => {{getter}};

			""";

		if (!field.IsInitOnly && !field.IsLiteral) {
			declaration += $"\t\tset => {setter};\n";
		}

		declaration += "\t}\n";

		return declaration;
	}

	private static string toMethodDeclaration(
		MethodInfo method,
		int overloadIndex,
		out string? reflectionMemberDeclaration
	) {
		var isReadOnlyReturnType = method.ReturnTypeCustomAttributes
			.GetCustomAttributes(inherit: true)
			.OfType<IsReadOnlyAttribute>()
			.Any();

		var returnType = method.ReturnType.GetReturnTypeString(isReadOnlyReturnType);

		string genericParameters, methodName, where;
		if (method.IsGenericMethod) {
			method.GetGenericArguments().GetGenericParameterString(out genericParameters, out where);
			methodName = $"{method.Name}{genericParameters}";
		} else {
			genericParameters = string.Empty;
			methodName = method.Name;
			where = string.Empty;
		}

		var declaringType = method.DeclaringType!;
		var declaration = $"\tpublic {(method.IsStatic ? "static " : string.Empty)}{returnType} {methodName}(";

		var parameters = method.GetParameters();
		if (parameters.Length > 0) {
			declaration += $"\n\t\t{string.Join(",\n\t\t", method.GetParameterStrings())}\n\t";
		}

		declaration += $"){where} {{\n";

		if (method.IsPublic) {
			if (method.ReturnType == typeof(void)) {
				declaration += "\t\t";
			} else {
				declaration += $"\t\treturn {(method.ReturnType.IsByRef ? "ref " : string.Empty)}";
			}
			declaration += $"{getMemberAccessPrefix(declaringType, method.IsStatic)}.{methodName}(";
			if (parameters.Length > 0) {
				declaration += $"\n\t\t\t{string.Join(",\n\t\t\t", method.GetArgumentStrings())}\n\t\t";
			}
			declaration += ");\n";

			reflectionMemberDeclaration = null;
		} else {
			// オーバーロードがある場合は後ろに数字つける
			var reflectionMemberName = overloadIndex != -1 ?
				$"{method.Name}_{overloadIndex}" :
				method.Name;

			if (method.IsTypeVisible()) {
				// Delegate
				if (method.ReturnType == typeof(void)) {
					declaration += "\t\t";
				} else {
					declaration += $"\t\treturn {(method.ReturnType.IsByRef ? "ref " : string.Empty)}";
				}
				if (method.IsGenericMethod) {
					declaration += $"ReflectionMembers.{reflectionMemberName}_Generic{genericParameters}.Method(";
				} else {
					declaration += $"ReflectionMembers.{reflectionMemberName}_Method(";
				}

				IEnumerable<string> arguments;
				if (!method.IsStatic) {
					arguments = [instancePropertyName, ..method.GetArgumentStrings()];
				} else {
					arguments = method.GetArgumentStrings();
				}
				if (arguments.Any()) {
					declaration += $"\n\t\t\t{string.Join(",\n\t\t\t", arguments)}\n\t\t";
				}
				declaration += ");\n";

			} else {
				// Reflection
				var instance = method.IsStatic ? "null" : instancePropertyName;

				string result;
				if (method.ReturnType == typeof(void)) {
					result = string.Empty;
				} else {
					result = $"var result = {method.ReturnType.GetCastString()}";
				}

				string methodInfoAccess;
				if (method.IsGenericMethod) {
					methodInfoAccess = string.Format(
						"{0}.{1}({2})",
						reflectionMemberName,
						nameof(MethodInfo.MakeGenericMethod),
						string.Join(", ", method.GetGenericArguments().Select(x => $"typeof({x.Name})"))
					);
				} else {
					methodInfoAccess = reflectionMemberName;
				}

				if (parameters.Length > 0) {
					var parameterValues = string.Join(
						",\n\t\t\t",
						parameters.Select(parameter =>
							parameter.IsOut ? "null" : parameter.Name
						)
					);
					declaration += $$"""
								var parameters = new object[] {
									{{parameterValues}}
								};
								{{result}}ReflectionMembers.{{methodInfoAccess}}.{{nameof(MethodInfo.Invoke)}}({{instance}}, parameters);

						""";

					// out変数の割り当て
					declaration += string.Concat(
						parameters
							.Select((parameter, i) => (Index: i, Parameter: parameter))
							.Where(x => x.Parameter.IsOut)
							.Select(x => string.Format(
								"\t\t{0} = {1}parameters[{2}];\n",
								x.Parameter.Name,
								x.Parameter.ParameterType.GetElementType()!.GetCastString(),
								x.Index
							))
					);
				} else {
					declaration += $$"""
								{{result}}ReflectionMembers.{{methodInfoAccess}}.{{nameof(MethodInfo.Invoke)}}({{instance}}, parameters: null);

						""";
				}

				if (method.ReturnType != typeof(void)) {
					declaration += "\t\treturn result;\n";
				}
			}

			if (overloadIndex != -1) {
				string parameterTypes;
				if (parameters.Length > 0) {
					parameterTypes = $$"""
										types: [
											{{string.Join(
												",\n\t\t\t\t\t",
												parameters.Select(x => string.Format(
													"typeof({0}){1}",
													(x.ParameterType.IsByRef ? x.ParameterType.GetElementType()! : x.ParameterType).GetTypeName(),
													x.ParameterType.IsByRef ? $".{nameof(Type.MakeByRefType)}()" : string.Empty
												))
											)}}
										]
						""";
				} else {
					// パラメータがない場合は空配列 (Type.EmptyTypes) を指定
					parameterTypes = $"\t\t\t\ttypes: {typeof(Type).FullName}.{nameof(Type.EmptyTypes)}";
				}
				reflectionMemberDeclaration = $$"""
							public static {{typeof(MethodInfo).FullName}} {{reflectionMemberName}} { get; } =
								typeof({{declaringType.GetTypeName()}}).{{nameof(Type.GetMethod)}}(
									"{{method.Name}}",
									{{getBindingFlagsString(method.IsPublic, method.IsStatic)}},
									binder: null,
					{{parameterTypes}},
									modifiers: null
								);

					""";
			} else {
				reflectionMemberDeclaration = $$"""
							public static {{typeof(MethodInfo).FullName}} {{reflectionMemberName}} { get; } =
								typeof({{declaringType.GetTypeName()}}).{{nameof(Type.GetMethod)}}(
									"{{method.Name}}",
									{{getBindingFlagsString(method.IsPublic, method.IsStatic)}}
								);

					""";
			}

			if (method.IsTypeVisible()) {
				// 型が全部publicならデリゲート作る
				var declaringTypeName = declaringType.GetTypeName();
				var parameterStrings = method.GetParameterStrings(includeInstance: true);

				string indent, delegateTypeName, delegateName, methodInfoAccess;
				if (method.IsGenericMethod) {
					// ジェネリックメソッドの場合はジェネリッククラスを定義する
					// (ジェネリックのデリゲートは直接作れない)
					indent = "\t\t\t";
					delegateTypeName = "Delegate";
					delegateName = "Method";
					methodInfoAccess = string.Format(
						"{0}.{1}({2})",
						reflectionMemberName,
						nameof(MethodInfo.MakeGenericMethod),
						string.Join(", ", method.GetGenericArguments().Select(x => $"typeof({x.Name})"))
					);
					// クラス定義始め
					reflectionMemberDeclaration += $"\n\t\tpublic static class {reflectionMemberName}_Generic{genericParameters}{where} {{\n";
				} else {
					indent = "\t\t";
					delegateTypeName = $"{reflectionMemberName}_Delegate";
					delegateName = $"{reflectionMemberName}_Method";
					methodInfoAccess = reflectionMemberName;
				}

				var delegateDeclaration = string.Format(
					"{0}public delegate {1} {2}({3});",
					indent,
					returnType,
					delegateTypeName,
					parameterStrings.Any() ?
						$"\n{indent}\t{string.Join($",\n{indent}\t", parameterStrings)}\n{indent}" :
						string.Empty
				);

				var methodDeclaration = string.Format(
					"{0}public static {1} {2} {{ get; }} =\n{0}\t({1}){3}.{4}(typeof({1}));",
					indent,
					delegateTypeName,
					delegateName,
					methodInfoAccess,
					nameof(MethodInfo.CreateDelegate)
				);

				reflectionMemberDeclaration += $"\n{delegateDeclaration}\n\n{methodDeclaration}\n\n";

				if (method.IsGenericMethod) {
					// クラス定義終わり
					reflectionMemberDeclaration += "\t\t}\n";
				}
			}
		}

		declaration += "\t}\n";

		return declaration;
	}

	private static string getMemberAccessPrefix(Type declaringType, bool isStatic) {
		return isStatic ? declaringType.GetTypeName() : instancePropertyName;
	}

	private static string getBindingFlagsString(bool isPublic, bool isStatic) {
		return string.Format(
			"{0}.{1} | {0}.{2}",
			typeof(BindingFlags).FullName,
			isPublic ? BindingFlags.Public : BindingFlags.NonPublic,
			isStatic ? BindingFlags.Static : BindingFlags.Instance
		);
	}

}
