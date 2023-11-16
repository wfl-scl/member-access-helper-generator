namespace MemberAccessHelperGenerator;

internal static class TypeExtensions {

	public static bool IsStatic(this Type type) => type.IsAbstract && type.IsSealed;

}
