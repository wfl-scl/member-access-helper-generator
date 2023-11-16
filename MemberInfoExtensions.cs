using System.Reflection;
using System.Runtime.CompilerServices;

namespace MemberAccessHelperGenerator;

internal static class MemberInfoExtensions {

	/// <summary>コンパイラが生成した要素かどうか</summary>
	/// <param name="memberInfo">メンバー</param>
	/// <returns>コンパイラが生成した要素である場合は <see langword="true"/></returns>
	public static bool IsCompilerGenerated(this MemberInfo memberInfo) {
		return
			memberInfo.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) ||
			memberInfo.DeclaringType?.IsCompilerGenerated() == true;
	}

}
