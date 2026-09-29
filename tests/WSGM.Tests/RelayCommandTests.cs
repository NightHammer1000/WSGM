using WSGM.Core;

namespace WSGM.Tests;

/// <summary>The executable specification of <see cref="RelayCommand"/> and
/// <see cref="RelayCommand{T}"/>: always-executable forwarding, and the typed
/// variant's parameter conversion (wrong type rejected, null only for
/// nullable-capable T).</summary>
public sealed class RelayCommandTests
{
    [Fact]
    public void Execute_InvokesAction()
    {
        var count = 0;
        var command = new RelayCommand(() => count++);

        command.Execute(null);
        command.Execute("ignored parameter");

        Assert.Equal(2, count);
    }

    [Fact]
    public void CanExecute_IsAlwaysTrue()
    {
        var command = new RelayCommand(() => { });

        Assert.True(command.CanExecute(null));
        Assert.True(command.CanExecute("anything"));
    }

    [Fact]
    public void NullExecute_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RelayCommand(null!));
        Assert.Throws<ArgumentNullException>(() => new RelayCommand<string>(null!));
    }

    [Fact]
    public void Typed_Execute_PassesConvertedParameter()
    {
        var received = new List<string?>();
        var command = new RelayCommand<string>(received.Add);

        command.Execute("hello");

        Assert.Equal(new[] { "hello" }, received);
    }

    [Fact]
    public void Typed_WrongParameterType_RejectedByCanExecute_AndExecuteIsNoOp()
    {
        var count = 0;
        var command = new RelayCommand<string>(_ => count++);

        Assert.False(command.CanExecute(42));
        command.Execute(42);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Typed_ReferenceType_AcceptsNull()
    {
        string? received = "sentinel";
        var command = new RelayCommand<string>(p => received = p);

        Assert.True(command.CanExecute(null));
        command.Execute(null);

        Assert.Null(received);
    }

    [Fact]
    public void Typed_NonNullableValueType_RejectsNull()
    {
        var count = 0;
        var command = new RelayCommand<int>(_ => count++);

        Assert.False(command.CanExecute(null));
        command.Execute(null);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Typed_NullableValueType_AcceptsNullAndValue()
    {
        var received = new List<int?>();
        var command = new RelayCommand<int?>(received.Add);

        Assert.True(command.CanExecute(null));
        Assert.True(command.CanExecute(7));
        command.Execute(null);
        command.Execute(7);

        Assert.Equal(new int?[] { null, 7 }, received);
    }

    [Fact]
    public void Typed_ValueType_AcceptsBoxedValue()
    {
        var received = new List<int>();
        var command = new RelayCommand<int>(received.Add);

        Assert.True(command.CanExecute(5));
        command.Execute(5);

        Assert.Equal(new[] { 5 }, received);
    }
}
