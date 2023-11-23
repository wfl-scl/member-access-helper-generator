namespace TestAssembly;

public class PublicClass {

	public static int PublicStaticIntProperty { get; set; } = 1;

	public int PublicIntProperty { get; set; } = 2;

	public int PublicPrivateIntProperty { get; private set; } = 3;

	public List<int>? PublicGenericProperty { get; set; } = [4];

	public ref int PublicRefIntProperty {
		get => ref privateIntField;
	}

	public ref readonly int PublicRefReadonlyIntProperty {
		get => ref privateIntField;
	}

	internal int InternalIntProperty { get; set; } = 5;

	internal InternalStruct InternalUnknownProperty { get; set; } = new() { Value = 6 };

	internal ref InternalStruct InternalRefUnknownProperty {
		get => ref privateUnknownField;
	}


	private int privateIntField = 7;

	private InternalStruct privateUnknownField = new() { Value = 8 };


	public void PublicVoidMethod() {
		privateIntField = 350;
	}

	public ref readonly int PublicRefMethod(
		in int inParameter,
		out int outParameter,
		ref int refParameter
	) {
		outParameter = inParameter;
		refParameter = 100;
		return ref privateIntField;
	}

	internal static InternalStruct InternalStaticUnknownMethod(
		int intParameter,
		InternalStruct unknownParameter
	) {
		return new() {
			Value = intParameter + unknownParameter.Value
		};
	}

}
