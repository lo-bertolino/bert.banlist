namespace Sample.App.Commands;

/// <summary>Banned by the sample's BannedSymbols.xml in favor of <see cref="AsyncRelayCommand"/>.</summary>
public class RelayCommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    public void Execute() => _execute();
}

public class AsyncRelayCommand
{
    private readonly Func<Task> _execute;

    public AsyncRelayCommand(Func<Task> execute) => _execute = execute;

    public Task ExecuteAsync() => _execute();
}
