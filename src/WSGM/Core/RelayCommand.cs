using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace WSGM.Core;

/// <summary>A minimal always-executable <see cref="ICommand"/> that forwards
/// <see cref="Execute"/> to a captured delegate, so XAML buttons can bind
/// commands without any UI-framework dependency.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    /// <summary>Creates a command around <paramref name="execute"/>.</summary>
    /// <param name="execute">The action invoked by <see cref="Execute"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="execute"/> is null.</exception>
    public RelayCommand(Action execute) =>
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    /// <summary>Never raised: the command is always executable.</summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    /// <summary>Always true. The <paramref name="parameter"/> is ignored.</summary>
    /// <param name="parameter">Unused; parameterless commands take no argument.</param>
    public bool CanExecute(object? parameter) => true;

    /// <summary>Invokes the captured action. The <paramref name="parameter"/> is ignored.</summary>
    /// <param name="parameter">Unused; parameterless commands take no argument.</param>
    public void Execute(object? parameter) => _execute();
}

/// <summary>An asynchronous command that disables itself while its operation is running and
/// observes every failure at the unavoidable async-void <see cref="ICommand.Execute"/> boundary.</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private int _running;

    /// <summary>Creates a serialized asynchronous command.</summary>
    public AsyncRelayCommand(Func<Task> execute) =>
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => Volatile.Read(ref _running) == 0;

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A command implementation should normally report its own user-facing
            // failure. This last boundary guarantees a forgotten catch never tears
            // down Avalonia or becomes an unobserved Task.
            Log.Error("Asynchronous command failed", ex);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>The typed-parameter companion of <see cref="RelayCommand"/>: the
/// command parameter is converted to <typeparamref name="T"/> before the delegate
/// runs. A parameter of the wrong type makes <see cref="CanExecute"/> return false
/// and <see cref="Execute"/> a no-op (never a crash — UI frameworks may invoke
/// Execute without a prior CanExecute check). A null parameter is accepted only
/// when <typeparamref name="T"/> can represent null (reference or nullable value
/// type).</summary>
/// <typeparam name="T">The expected command-parameter type.</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;

    /// <summary>Creates a command around <paramref name="execute"/>.</summary>
    /// <param name="execute">The action invoked by <see cref="Execute"/> with the
    /// converted parameter.</param>
    /// <exception cref="ArgumentNullException"><paramref name="execute"/> is null.</exception>
    public RelayCommand(Action<T?> execute) =>
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    /// <summary>Never raised: executability depends only on the parameter type.</summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    /// <summary>False only when <paramref name="parameter"/> cannot be converted
    /// to <typeparamref name="T"/> (wrong type, or null for a non-nullable value type).</summary>
    /// <param name="parameter">The command parameter to convert and test.</param>
    public bool CanExecute(object? parameter) => TryConvert(parameter, out _);

    /// <summary>Invokes the captured action with the converted parameter. Does
    /// nothing when the parameter cannot be converted to <typeparamref name="T"/>.</summary>
    /// <param name="parameter">The command parameter to convert and pass on.</param>
    public void Execute(object? parameter)
    {
        if (TryConvert(parameter, out var value))
        {
            _execute(value);
        }
    }

    private static bool TryConvert(object? parameter, out T? value)
    {
        if (parameter is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        // Null is a valid value only for types that can actually hold it:
        // reference types and Nullable<T> have default == null; a non-nullable
        // value type must reject null instead of silently becoming default(T).
        return parameter is null && default(T) is null;
    }
}
