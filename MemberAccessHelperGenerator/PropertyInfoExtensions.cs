using System.Reflection;
using System.Runtime.CompilerServices;

namespace MemberAccessHelperGenerator;

internal static class PropertyInfoExtensions {

	public static bool IsPublic(this PropertyInfo property) {
		return
			(property.CanRead && property.GetMethod!.IsPublic) ||
			(property.CanWrite && property.SetMethod!.IsPublic);
	}

	public static bool IsStatic(this PropertyInfo property) {
		if (property.CanRead) {
			return property.GetMethod!.IsStatic;
		} else {
			return property.SetMethod!.IsStatic;
		}
	}

	public static bool IsReadOnly(this PropertyInfo property) =>
		property.CustomAttributes.Any(x => x.AttributeType == typeof(IsReadOnlyAttribute));

}
