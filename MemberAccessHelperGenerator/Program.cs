using MemberAccessHelperGenerator;

var app = ConsoleApp.Create(args);
app.AddCommands<Generator>();
app.Run();
