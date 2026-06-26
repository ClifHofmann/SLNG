using SLNG.Net;

// Minimal login probe — the tangible M0-2 artifact. Performs a real grid login and
// prints the result. Defaults to a local OpenSim grid; pass creds via args or env.
//
//   dotnet run --project tools/SLNG.LoginProbe -- --grid http://127.0.0.1:9000/ \
//       --first Test --last User --pass secret
//   (or set SLNG_GRID / SLNG_FIRST / SLNG_LAST / SLNG_PASS)

string? Arg(string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }
    return null;
}

string grid = Arg("--grid") ?? Environment.GetEnvironmentVariable("SLNG_GRID") ?? LoginCredentials.OpenSimLocalLoginUri;
string first = Arg("--first") ?? Environment.GetEnvironmentVariable("SLNG_FIRST") ?? "";
string last = Arg("--last") ?? Environment.GetEnvironmentVariable("SLNG_LAST") ?? "Resident";
string pass = Arg("--pass") ?? Environment.GetEnvironmentVariable("SLNG_PASS") ?? "";

if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(pass))
{
    Console.Error.WriteLine("usage: slng-login --grid <uri> --first <name> --last <name> --pass <password>");
    Console.Error.WriteLine("       (or set SLNG_GRID / SLNG_FIRST / SLNG_LAST / SLNG_PASS)");
    return 2;
}

Console.WriteLine($"Logging in {first} {last} @ {grid} ...");

using var session = new GridSession();
var result = await session.LoginAsync(new LoginCredentials
{
    FirstName = first,
    LastName = last,
    Password = pass,
    GridLoginUri = grid,
});

if (result.Success)
{
    Console.WriteLine($"OK  agent={result.AgentId}  session={result.SessionId}");
    if (!string.IsNullOrWhiteSpace(result.Message))
    {
        Console.WriteLine($"message: {result.Message}");
    }
    session.Logout();
    return 0;
}

Console.WriteLine($"FAILED  error={result.ErrorKey}  message={result.Message}");
return 1;
