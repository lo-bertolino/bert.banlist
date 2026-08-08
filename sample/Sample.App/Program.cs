using Sample.App.Commands;

// Both lines below produce BAN0001 warnings; Ctrl+. on RelayCommand offers the
// AsyncRelayCommand replacement, and Ctrl+. on any other symbol offers to ban it.
var save = new RelayCommand(() => Console.WriteLine("saved"));
save.Execute();

Console.WriteLine(DateTime.Now);
