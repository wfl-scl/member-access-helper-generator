using TestAssembly;

namespace MemberAccessHelperGenerator.Tests;

public class Helper {

	[Fact]
	public void PublicStaticIntProperty() {
		PublicClassHelper.PublicStaticIntProperty = 123;
		PublicClassHelper.PublicStaticIntProperty.Should().Be(123);
	}

	[Fact]
	public void PublicIntProperty() {
		PublicClassHelper helper = new(new()) {
			PublicIntProperty = 456
		};
		helper.PublicIntProperty.Should().Be(456);
	}

	[Fact]
	public void PublicPrivateIntProperty() {
		PublicClassHelper helper = new(new()) {
			PublicPrivateIntProperty = 789
		};
		helper.PublicPrivateIntProperty.Should().Be(789);
	}

	[Fact]
	public void PublicGenericProperty() {
		List<int> list = [100];
		PublicClassHelper helper = new(new()) {
			PublicGenericProperty = list
		};
		helper.PublicGenericProperty.Should().BeEquivalentTo(list);
	}

	[Fact]
	public void PublicRefIntProperty() {
		PublicClassHelper helper = new(new());
		ref var value = ref helper.PublicRefIntProperty;

		value = 1234;
		helper.PublicRefIntProperty.Should().Be(1234);

		value = 5678;
		helper.PublicRefIntProperty.Should().Be(5678);
	}

	[Fact]
	public void PublicRefReadonlyIntProperty() {
		PublicClassHelper helper = new(new());
		ref readonly var value = ref helper.PublicRefReadonlyIntProperty;

		helper.privateIntField = 1234;
		value.Should().Be(1234);

		helper.privateIntField = 5678;
		value.Should().Be(5678);
	}

	[Fact]
	public void InternalIntProperty() {
		PublicClassHelper helper = new(new()) {
			InternalIntProperty = 123
		};
		helper.InternalIntProperty.Should().Be(123);
	}

	[Fact]
	public void InternalRefIntProperty() {
		PublicClassHelper helper = new(new());
		ref var value = ref helper.InternalRefIntProperty;

		value = 1234;
		helper.InternalRefIntProperty.Should().Be(1234);

		value = 5678;
		helper.InternalRefIntProperty.Should().Be(5678);
	}

	[Fact]
	public void InternalUnknownProperty() {
		var value = createInternalStruct(456);
		PublicClassHelper helper = new(new()) {
			InternalUnknownProperty = value
		};
		helper.InternalUnknownProperty.ToString().Should().Be(value.ToString());
	}

	[Fact]
	public void InternalRefUnknownProperty() {
		PublicClassHelper helper = new(new());
		// まだ値設定できないので初期値だけ確認する
		helper.InternalRefUnknownProperty.ToString().Should().Be("8");
	}

	[Fact]
	public void privateIntField() {
		PublicClassHelper helper = new(new()) {
			privateIntField = 1234
		};
		helper.privateIntField.Should().Be(1234);
	}

	[Fact]
	public void privateUnknownField() {
		var value = createInternalStruct(5678);
		PublicClassHelper helper = new(new()) {
			privateUnknownField = value
		};
		helper.privateUnknownField.ToString().Should().Be(value.ToString());
	}

	[Fact]
	public void PublicVoidMethod() {
		PublicClassHelper helper = new(new()) {
			privateIntField = 123
		};
		helper.privateIntField.Should().Be(123);
		helper.PublicVoidMethod();
		helper.privateIntField.Should().Be(350);
	}

	[Fact]
	public void PublicIntMethod() {
		PublicClassHelper helper = new(new()) {
			PublicIntProperty = 123
		};
		helper.PublicIntMethod(456).Should().Be(579);
	}

	[Fact]
	public void PublicRefMethod() {
		PublicClassHelper helper = new(new()) {
			privateIntField = 123
		};
		int inParameter = 456;
		int refParameter = 789;
		ref readonly var result = ref helper.PublicRefMethod(
			in inParameter,
			out var outParameter,
			ref refParameter
		);
		outParameter.Should().Be(inParameter);
		refParameter.Should().Be(100);
		result.Should().Be(123);

		helper.privateIntField = 1234;
		result.Should().Be(1234);
	}

	[Fact]
	public void InternalStaticUnknownMethod() {
		var intParameter = 123;
		var unknownParameter = createInternalStruct(456);
		var result = PublicClassHelper.InternalStaticUnknownMethod(intParameter, unknownParameter);
		result.ToString().Should().Be("579");
	}

	private static object createInternalStruct(int fieldValue) {
		var property = PublicClassHelper.ReflectionMembers.InternalUnknownProperty;
		var instance = Activator.CreateInstance(property.PropertyType)!;
		var field = property.PropertyType.GetField("Value")!;
		field.SetValue(instance, fieldValue);
		return instance;
	}

}
