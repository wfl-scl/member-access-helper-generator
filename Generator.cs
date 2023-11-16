using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MemberAccessHelperGenerator;

internal static partial class Generator {

	private const string defaultOutputFolder = @".\Generated";
	private const string instancePropertyName = "InstanceForHelper";

	private const BindingFlags generateTargetBindingFlags =
		BindingFlags.Public |
		BindingFlags.NonPublic |
		BindingFlags.Static |
		BindingFlags.Instance;

	private static readonly Type[] excludeTypes = new[] {
		typeof(Delegate),
		typeof(Attribute)
	};

	[GeneratedRegex(@"`(\d+)")]
	private static partial Regex genericTypeArgumentRegex();


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
			var declaration = toPropertyDeclaration(property, type, out var reflectionMemberDeclaration);
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
			var declaration = toFieldDeclaration(field, type, out var reflectionMemberDeclaration);
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
					isSameSignature(x, method) &&
					x.DeclaringType?.IsAssignableTo(method.DeclaringType) == true
				)
			) {
				continue;
			}

			// オーバーロードがある場合はReflectionMembersの名前の後ろに数字をつける
			var overloads = methodInfo
				.Where(x =>
					x == method ||
					(x.Name == method.Name && !isSameSignature(x, method))
				)
				.ToArray();

			var overloadIndex = (overloads.Length >= 2) ?
				Array.IndexOf(overloads, method) :
				-1;

			var declaration = toMethodDeclaration(method, overloadIndex, type, out var reflectionMemberDeclaration);
			if (!string.IsNullOrEmpty(methods)) {
				methods += "\n";
			}
			methods += declaration;
			addReflectionMemberIfNeeded(reflectionMemberDeclaration);
		}

		var namespaceLines = string.IsNullOrEmpty(type.Namespace) ?
			string.Empty :
			$"\nnamespace {type.Namespace};\n";

		var helperClassName = $"{getTypeName(type).Replace(".", "_").Replace("<", "_").Replace(">", "_").Replace(", ", "_")}Helper";
		if (type.Namespace != null) {
			helperClassName = helperClassName.Replace($"type.Namespace.", string.Empty);
		}
		var typeFullName = getTypeName(type);

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
			$"{helperClassName.Replace("<", "_").Replace(">", "_").Replace(", ", "_")}.cs"
		);
		File.WriteAllText(sourcePath, source);

		return true;
	}

	private static string toPropertyDeclaration(
		PropertyInfo property,
		Type declaringType,
		out string? reflectionMemberDeclaration
	) {
		bool isStatic;
		if (property.CanRead) {
			isStatic = property.GetMethod!.IsStatic;
		} else {
			isStatic = property.SetMethod!.IsStatic;
		}

		var isReadOnly = property.CustomAttributes.Any(x => x.AttributeType == typeof(IsReadOnlyAttribute));
		var propertyType = getReturnTypeString(property.PropertyType, isReadOnly);
		var declaration = $"\tpublic {(isStatic ? "static " : string.Empty)}{propertyType} {property.Name} {{\n";

		var reflection = false;

		if (property.CanRead) {
			string getter;
			if (property.GetMethod!.IsPublic) {
				var prefix = getMemberPrefix(declaringType, isStatic);
				getter = $"{prefix}.{property.Name}";
			} else {
				var instance = isStatic ? "null" : instancePropertyName;
				var cast = getCastString(property.PropertyType);
				getter = $"{cast}ReflectionMembers.{property.Name}.{nameof(PropertyInfo.GetValue)}({instance})";
				reflection = true;
			}
			declaration += $"\t\tget => {getter};\n";
		}

		if (property.CanWrite) {
			string setter;
			if (property.SetMethod!.IsPublic) {
				var prefix = getMemberPrefix(declaringType, isStatic);
				setter = $"{prefix}.{property.Name} = value";
			} else {
				var instance = isStatic ? "null" : instancePropertyName;
				setter = $"ReflectionMembers.{property.Name}.{nameof(PropertyInfo.SetValue)}({instance}, value)";
				reflection = true;
			}
			declaration += $"\t\tset => {setter};\n";
		}

		if (reflection) {
			reflectionMemberDeclaration = $$"""
						public static readonly {{typeof(PropertyInfo).FullName}} {{property.Name}} =
							typeof({{getTypeName(declaringType)}}).{{nameof(Type.GetProperty)}}("{{property.Name}}", {{getBindingFlagsString(isStatic)}});

				""";
		} else {
			reflectionMemberDeclaration = null;
		}

		declaration += "\t}\n";
		return declaration;
	}

	private static string toFieldDeclaration(
		FieldInfo field,
		Type declaringType,
		out string? reflectionMemberDeclaration
	) {
		var typeName = getTypeName(field.FieldType);

		string getter, setter;
		if (field.IsPublic) {
			var prefix = getMemberPrefix(declaringType, field.IsStatic);
			getter = $"{prefix}.{field.Name}";
			setter = $"{prefix}.{field.Name} = value";

			reflectionMemberDeclaration = null;
		} else {
			var instance = field.IsStatic ? "null" : instancePropertyName;
			var cast = getCastString(field.FieldType);
			getter = $"{cast}ReflectionMembers.{field.Name}.{nameof(PropertyInfo.GetValue)}({instance})";
			setter = $"ReflectionMembers.{field.Name}.{nameof(PropertyInfo.SetValue)}({instance}, value)";

			reflectionMemberDeclaration = $$"""
						public static readonly {{typeof(FieldInfo).FullName}} {{field.Name}} =
							typeof({{getTypeName(declaringType)}}).{{nameof(Type.GetField)}}("{{field.Name}}", {{getBindingFlagsString(field.IsStatic)}});

				""";
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
		Type declaringType,
		out string? reflectionMemberDeclaration
	) {
		var isReadOnlyReturnType = method.ReturnTypeCustomAttributes
			.GetCustomAttributes(inherit: true)
			.OfType<IsReadOnlyAttribute>()
			.Any();

		var returnType = getReturnTypeString(method.ReturnType, isReadOnlyReturnType);

		string methodName, where;
		if (method.IsGenericMethod) {
			var genericArguments = method.GetGenericArguments();
			methodName = $"{method.Name}<{string.Join(",", genericArguments.Select(getTypeName))}>";
			where = string.Concat(
				genericArguments
					.Select(type => (
						GenericTypeName: type.Name,
						Parameters: getWhereParameters(type)
					))
					.Where(x => x.Parameters != null)
					.Select(x => $" where {x.GenericTypeName} : {x.Parameters}")
			);
		} else {
			methodName = method.Name;
			where = string.Empty;
		}

		var declaration = $"\tpublic {(method.IsStatic ? "static " : string.Empty)}{returnType} {methodName}(";

		var parameters = method.GetParameters();
		if (parameters.Length > 0) {
			var parameterDeclaration = string.Join(
				",\n\t\t",
				parameters.Select(parameter => {
					var parameterType = parameter.ParameterType;
					var prefix = string.Empty;
					if (parameter.IsIn) {
						prefix = "in ";
						parameterType = parameterType.GetElementType()!;
					} else if (parameter.IsOut) {
						prefix = "out ";
						parameterType = parameterType.GetElementType()!;
					} else if (parameterType.IsByRef) {
						prefix = "ref ";
						parameterType = parameterType.GetElementType()!;
					}
					var suffix = string.Empty;
					if (parameter.HasDefaultValue) {
						suffix = $" = {valueToLiteral(parameter.DefaultValue, parameterType)}";
					}
					return $"{prefix}{getTypeName(parameterType)} {parameter.Name}{suffix}";
				})
			);

			declaration += $"\n\t\t{parameterDeclaration}\n\t";
		}

		declaration += $"){where} {{\n";

		if (method.IsPublic) {
			if (method.ReturnType == typeof(void)) {
				declaration += "\t\t";
			} else {
				declaration += "\t\treturn ";
			}
			declaration += $"{getMemberPrefix(declaringType, method.IsStatic)}.{methodName}(";
			if (parameters.Length > 0) {
				var parameterValues = string.Join(
					",\n\t\t\t",
					parameters.Select(parameter => {
						var prefix = string.Empty;
						if (parameter.IsIn) {
							prefix = "in ";
						} else if (parameter.IsOut) {
							prefix = "out ";
						} else if (parameter.ParameterType.IsByRef) {
							prefix = "ref ";
						}
						return $"{prefix}{parameter.Name}";
					})
				);
				declaration += $"\n\t\t\t{parameterValues}\n\t\t";
			}
			declaration += ");\n";

			reflectionMemberDeclaration = null;
		} else {
			var instance = method.IsStatic ? "null" : instancePropertyName;

			string result;
			if (method.ReturnType == typeof(void)) {
				result = string.Empty;
			} else {
				result = $"var result = {getCastString(method.ReturnType)}";
			}

			// オーバーロードがある場合は後ろに数字つける
			var reflectionMemberName = overloadIndex != -1 ?
				$"{method.Name}_{overloadIndex}" :
				method.Name;

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
							{{result}}ReflectionMembers.{{reflectionMemberName}}.{{nameof(MethodInfo.Invoke)}}({{instance}}, parameters);

					""";

				// out変数の割り当て
				declaration += string.Concat(
					parameters
						.Select((parameter, i) => (Index: i, Parameter: parameter))
						.Where(x => x.Parameter.IsOut)
						.Select(x => string.Format(
							"\t\t{0} = {1}parameters[{2}];\n",
							x.Parameter.Name,
							getCastString(x.Parameter.ParameterType.GetElementType()!),
							x.Index
						))
				);
			} else {
				declaration += $$"""
							{{result}}ReflectionMembers.{{reflectionMemberName}}.{{nameof(MethodInfo.Invoke)}}({{instance}}, parameters: null);

					""";
			}

			if (method.ReturnType != typeof(void)) {
				declaration += "\t\treturn result;\n";
			}

			if (overloadIndex != -1) {
				string parameterTypes;
				if (parameters.Length > 0) {
					parameterTypes = $$"""
										types: new {{typeof(Type).FullName}}[] {
											{{
												string.Join(
													",\n\t\t\t\t\t",
													parameters.Select(x => string.Format(
														"typeof({0}){1}",
														getTypeName(x.ParameterType.IsByRef ? x.ParameterType.GetElementType()! : x.ParameterType),
														x.ParameterType.IsByRef ? $".{nameof(Type.MakeByRefType)}()" : string.Empty
													))
												)}}
										}
						""";
				} else {
					// パラメータがない場合は空配列 (Type.EmptyTypes) を指定
					parameterTypes = $"\t\t\t\ttypes: {typeof(Type).FullName}.{nameof(Type.EmptyTypes)}";
				}
				reflectionMemberDeclaration = $$"""
							public static readonly {{typeof(MethodInfo).FullName}} {{reflectionMemberName}} =
								typeof({{getTypeName(declaringType)}}).{{nameof(Type.GetMethod)}}(
									"{{method.Name}}",
									{{getBindingFlagsString(method.IsStatic)}},
									binder: null,
					{{parameterTypes}},
									modifiers: null
								);

					""";
			} else {
				reflectionMemberDeclaration = $$"""
							public static readonly {{typeof(MethodInfo).FullName}} {{reflectionMemberName}} =
								typeof({{getTypeName(declaringType)}}).{{nameof(Type.GetMethod)}}("{{method.Name}}", {{getBindingFlagsString(method.IsStatic)}});

					""";
			}
		}

		declaration += "\t}\n";

		return declaration;
	}

	private static string getTypeName(Type type) {
		if (!type.IsVisible) {
			return "object";
		}
		if (type.IsGenericParameter) {
			// Tとかはそのままにする
			return type.Name;
		} else if (type.IsArray) {
			var commaCount = type.GetArrayRank() - 1;
			var elementTypeName = getTypeName(type.GetElementType()!);
			if (commaCount > 0) {
				return $"{elementTypeName}[{new string(',', commaCount)}]";
			} else {
				return $"{elementTypeName}[]";
			}
		} else if (type == typeof(void)) {
			return "void";
		} else if (type == typeof(sbyte)) {
			return "sbyte";
		} else if (type == typeof(byte)) {
			return "byte";
		} else if (type == typeof(short)) {
			return "short";
		} else if (type == typeof(ushort)) {
			return "ushort";
		} else if (type == typeof(int)) {
			return "int";
		} else if (type == typeof(uint)) {
			return "uint";
		} else if (type == typeof(long)) {
			return "long";
		} else if (type == typeof(ulong)) {
			return "ulong";
		} else if (type == typeof(char)) {
			return "char";
		} else if (type == typeof(float)) {
			return "float";
		} else if (type == typeof(double)) {
			return "double";
		} else if (type == typeof(bool)) {
			return "bool";
		} else if (type == typeof(decimal)) {
			return "decimal";
		} else if (type == typeof(string)) {
			return "string";
		}

		string typeName;
		if (type.FullName != null) {
			typeName = type.FullName.Replace('+', '.');
			if (!type.IsGenericType) {
				return typeName;
			}
			var argumentsIndex = typeName.IndexOf('[');
			if (argumentsIndex == -1) {
				// ジェネリック型でも型パラメーター情報が無い場合がある
				// (ジェネリックメソッドの引数や戻り値など)
				return typeName;
			}
			// 型パラメーターを表す文字列を削除する
			typeName = typeName[..argumentsIndex];

		} else {
			if (type.IsNested) {
				typeName = $"{getTypeName(type.DeclaringType!)}.{type.Name}";
			} else {
				if (type.Namespace != null) {
					typeName = $"{type.Namespace}.{type.Name}";
				} else {
					typeName = type.Name;
				}
			}
		}

		if (type.IsGenericType) {
			// 型パラメーターを解決する
			// (List`1 とかは List<int> のように変換される)
			var genericTypeArguments = type.GetGenericArguments();
			int i = 0;
			while (i < genericTypeArguments.Length) {
				var match = genericTypeArgumentRegex().Match(typeName);
				var argumentsCount = int.Parse(match.Groups[1].Value);
				var arguments = $"<{string.Join(", ", genericTypeArguments.Skip(i).Take(argumentsCount).Select(getTypeName))}>";

				typeName = typeName.Remove(match.Index, match.Length).Insert(match.Index, arguments);

				i += argumentsCount;
			}
		}
		return typeName;
	}

	private static string valueToLiteral<T>(T value, Type type) {
		if (!type.IsVisible) {
			return "default";
		}
		return value switch {
			null => type.IsValueType ? "default" : "null",
			string str => $"\"{str}\"",
			char c => $"'{c}'",
			bool b => b.ToString().ToLower(),
			float f => $"{f}f",
			sbyte or byte or short or ushort or int or uint or long or ulong or double or decimal => value.ToString()!,
			Enum flags => string.Join(
				" | ",
				flags.ToString().Split(", ").Select(flag => $"{getTypeName(flags.GetType())}.{flag}")
			),
			_ => throw new NotImplementedException(
				$"Unknown value: {value.GetType().Name} ({value})"
			)
		};
	}

	private static bool isSameSignature(MethodInfo a, MethodInfo b) {
		return
			a.Name == b.Name &&
			a.GetParameters().SequenceEqual(b.GetParameters()) &&
			a.GetGenericArguments().SequenceEqual(b.GetGenericArguments());
	}

	private static string? getWhereParameters(Type type) {
		string? parameters = null;
		void addParameter(string parameter) {
			if (parameters == null) {
				parameters = parameter;
			} else {
				parameters += $", {parameter}";
			}
		}

		var attributes = type.GenericParameterAttributes;

		if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)) {
			addParameter("class");
		} else if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)) {
			addParameter("struct");
		}
		foreach (var typeParameter in type.GetGenericParameterConstraints()) {
			if (typeParameter == typeof(ValueType)) {
				// struct 制約は型としても入ってる？
				continue;
			}
			addParameter(getTypeName(typeParameter));
		}
		if (
			!attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint) &&
			attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)
		) {
			addParameter("new()");
		}

		return parameters;
	}

	private static string getMemberPrefix(Type declaringType, bool isStatic) {
		return isStatic ? getTypeName(declaringType) : instancePropertyName;
	}

	private static string getBindingFlagsString(bool isStatic) {
		var flagsTypeName = typeof(BindingFlags).FullName;
		return $"{flagsTypeName}.{BindingFlags.NonPublic} | {flagsTypeName}.{(isStatic ? BindingFlags.Static : BindingFlags.Instance)}";
	}

	private static string getCastString(Type resultType) {
		return resultType != typeof(object) ? $"({getTypeName(resultType)})" : string.Empty;
	}

	private static string getReturnTypeString(Type type, bool isReadOnly) {
		return string.Format(
			"{0}{1}{2}",
			type.IsByRef ? "ref " : string.Empty,
			isReadOnly ? "readonly " : string.Empty,
			getTypeName(type.IsByRef ? type.GetElementType()! : type)
		);
	}

}
