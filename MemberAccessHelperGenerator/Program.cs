using MemberAccessHelperGenerator;

var app = ConsoleApp.Create(args);
app.AddRootCommand(Generator.Run);
app.Run();
