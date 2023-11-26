using System.Reflection;
using System.Text.RegularExpressions;

namespace MemberAccessHelperGenerator;

internal static partial class TypeExtensions {

	[GeneratedRegex(@"`(\d+)")]
	private static partial Regex genericTypeArgumentRegex();


	public static bool IsStatic(this Type type) => type.IsAbstract && type.IsSealed;

	public static string GetTypeName(this Type type) {
		if (!type.IsVisible) {
			return "object";
		}
		if (type.IsGenericParameter) {
			// Tとかはそのままにする
			return type.Name;
		} else if (type.IsArray) {
			var commaCount = type.GetArrayRank() - 1;
			var elementTypeName = type.GetElementType()!.GetTypeName();
			if (commaCount > 0) {
				return $"{elementTypeName}[{new string(',', commaCount)}]";
			} else {
				return $"{elementTypeName}[]";
			}
		} else if (type == typeof(void)) {
			return "void";
		} else if (type == typeof(object)) {
			return "object";
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
				typeName = $"{type.DeclaringType!.GetTypeName()}.{type.Name}";
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
				var arguments = $"<{string.Join(", ", genericTypeArguments.Skip(i).Take(argumentsCount).Select(GetTypeName))}>";

				typeName = typeName.Remove(match.Index, match.Length).Insert(match.Index, arguments);

				i += argumentsCount;
			}
		}
		return typeName;
	}

	public static string GetCastString(this Type type) {
		// 不明な型はobjectのまま返すのでキャストしない
		if (!type.IsVisible || type == typeof(object)) {
			return string.Empty;
		}
		// refは元の型にキャスト
		return $"({(type.IsByRef ? type.GetElementType()! : type).GetTypeName()})";
	}

	public static string GetReturnTypeString(this Type type, bool isReadOnly) {
		if (!type.IsVisible) {
			return "object";
		}
		return string.Format(
			"{0}{1}{2}",
			type.IsByRef ? "ref " : string.Empty,
			isReadOnly ? "readonly " : string.Empty,
			(type.IsByRef ? type.GetElementType()! : type).GetTypeName()
		);
	}

	public static string ValueToLiteral<T>(this Type type, T value) {
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
				flags.ToString().Split(", ").Select(flag => $"{flags.GetType().GetTypeName()}.{flag}")
			),
			_ => throw new NotImplementedException(
				$"Unknown value: {value.GetType().Name} ({value})"
			)
		};
	}

	public static void GetGenericParameterString(
		this IEnumerable<Type> types,
		out string parameters,
		out string where
	) {
		parameters = $"<{string.Join(", ", types.Select(type => type.Name))}>";
		where = string.Concat(
			types
				.Select(type => (
					GenericTypeName: type.Name,
					Parameters: getWhereParameters(type)
				))
				.Where(x => x.Parameters != null)
				.Select(x => $" where {x.GenericTypeName} : {x.Parameters}")
		);
	}

	private static string? getWhereParameters(Type genericType) {
		string? parameters = null;
		void addParameter(string parameter) {
			if (parameters == null) {
				parameters = parameter;
			} else {
				parameters += $", {parameter}";
			}
		}

		var attributes = genericType.GenericParameterAttributes;

		if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)) {
			addParameter("class");
		} else if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)) {
			addParameter("struct");
		}
		foreach (var typeParameter in genericType.GetGenericParameterConstraints()) {
			if (
				// struct 制約は型としても入ってるっぽいので無視する
				typeParameter == typeof(ValueType) ||
				// 不明な型の指定は無視する
				!typeParameter.IsVisible
			) {
				continue;
			}
			addParameter(typeParameter.GetTypeName());
		}
		if (
			!attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint) &&
			attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)
		) {
			addParameter("new()");
		}

		return parameters;
	}

}
