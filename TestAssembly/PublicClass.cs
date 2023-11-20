namespace TestAssembly;

public class PublicClass {

	public static int PublicStaticIntProperty { get; set; }

	public int PublicIntProperty { get; set; }

	public int PublicPrivateIntProperty { get; private set; }

	public List<int>? PublicGenericProperty { get; set; }

	public ref int PublicRefIntProperty {
		get => ref privateIntField;
	}

	public ref readonly int PublicRefReadonlyIntProperty {
		get => ref privateIntField;
	}

	internal int InternalIntProperty { get; set; }

	internal InternalStruct InternalUnknownProperty { get; set; }

	internal ref InternalStruct InternalRefUnknownProperty {
		get => ref privateUnknownField;
	}


	private int privateIntField = 5;

	private InternalStruct privateUnknownField;


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
