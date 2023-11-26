using System.Reflection;

namespace MemberAccessHelperGenerator;

internal static class MethodInfoExtensions {

	public static bool IsTypeVisible(this MethodInfo method) {
		if (!method.ReturnType.IsVisible) {
			return false;
		}
		if (!method.GetParameters().All(parameter => parameter.ParameterType.IsVisible)) {
			return false;
		}
		if (method.IsGenericMethod) {
			var isVisible = method.GetGenericArguments()
				.SelectMany(type => type.GetGenericParameterConstraints())
				.All(type => type.IsVisible);

			if (!isVisible) {
				return false;
			}
		}
		return true;
	}

	public static bool IsSameSignature(this MethodInfo method, MethodInfo other) {
		return
			method.Name == other.Name &&
			method.GetParameters().SequenceEqual(other.GetParameters()) &&
			method.GetGenericArguments().SequenceEqual(other.GetGenericArguments());
	}

	public static IEnumerable<string> GetParameterStrings(this MethodInfo method, bool includeInstance = false) {
		if (!method.IsStatic && includeInstance) {
			yield return $"{method.DeclaringType!.GetTypeName()} instance";
		}
		var parameters = method.GetParameters();
		if (parameters.Length <= 0) {
			yield break;
		}
		var parameterStrings = parameters.Select(parameter => {
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
				suffix = $" = {parameterType.ValueToLiteral(parameter.DefaultValue)}";
			}
			return $"{prefix}{parameterType.GetTypeName()} {parameter.Name}{suffix}";
		});
		foreach (var str in parameterStrings) {
			yield return str;
		}
	}

	public static IEnumerable<string> GetArgumentStrings(this MethodInfo method) {
		var parameters = method.GetParameters();
		if (parameters.Length <= 0) {
			return Enumerable.Empty<string>();
		}
		return parameters.Select(parameter => {
			var prefix = string.Empty;
			if (parameter.IsIn) {
				prefix = "in ";
			} else if (parameter.IsOut) {
				prefix = "out ";
			} else if (parameter.ParameterType.IsByRef) {
				prefix = "ref ";
			}
			return $"{prefix}{parameter.Name}";
		});
	}

}
