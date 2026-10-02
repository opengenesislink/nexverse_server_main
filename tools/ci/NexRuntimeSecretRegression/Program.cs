using System;
using System.IO;
using Nini.Config;
using OpenSim.Framework;

internal static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static IConfigSource CreateConfig()
    {
        IniConfigSource source = new IniConfigSource();
        source.AddConfig("Environment").Set("NEXVERSE_DB_PASSWORD", string.Empty);
        source.AddConfig("DatabaseService").Set(
            "ConnectionString",
            "Data Source=localhost;Database=nexverse;User ID=nexverse;Password=${Environment|NEXVERSE_DB_PASSWORD};SslMode=None;");
        return source;
    }

    private static string ResolveConnectionString()
    {
        IConfigSource source = CreateConfig();
        Util.MergeEnvironmentToConfig(source);
        source.ReplaceKeyValues();
        return source.Configs["DatabaseService"].GetString("ConnectionString", string.Empty);
    }

    private static int Main()
    {
        string oldFile = Environment.GetEnvironmentVariable("NEXVERSE_ENV_FILE");
        string oldPassword = Environment.GetEnvironmentVariable("NEXVERSE_DB_PASSWORD");
        string temp = Path.Combine(
            Path.GetTempPath(),
            "nexverse-secret-regression-" + Guid.NewGuid().ToString("N") + ".env");

        try
        {
            File.WriteAllText(
                temp,
                "NEXVERSE_DB_PASSWORD=FileBang!2026" + Environment.NewLine);

            Environment.SetEnvironmentVariable("NEXVERSE_ENV_FILE", temp);
            Environment.SetEnvironmentVariable("NEXVERSE_DB_PASSWORD", null);

            string fromFile = ResolveConnectionString();
            Require(
                fromFile.Contains("Password=FileBang!2026;", StringComparison.Ordinal),
                "runtime env file value containing ! was not expanded into the connection string");

            File.WriteAllText(
                temp,
                "NEXVERSE_DB_PASSWORD=ShouldNotWin!2026" + Environment.NewLine);
            Environment.SetEnvironmentVariable(
                "NEXVERSE_DB_PASSWORD",
                "ProcessWins!2026");

            string fromProcess = ResolveConnectionString();
            Require(
                fromProcess.Contains("Password=ProcessWins!2026;", StringComparison.Ordinal),
                "process environment variable must take precedence over the runtime env file");
            Require(
                !fromProcess.Contains("ShouldNotWin!2026", StringComparison.Ordinal),
                "runtime env file unexpectedly overrode the process environment variable");

            File.WriteAllText(
                temp,
                "NEXVERSE_DB_PASSWORD=\"QuotedBang!2026\"" + Environment.NewLine);
            Environment.SetEnvironmentVariable("NEXVERSE_DB_PASSWORD", null);

            string quoted = ResolveConnectionString();
            Require(
                quoted.Contains("Password=QuotedBang!2026;", StringComparison.Ordinal),
                "quoted runtime env file value was not normalized correctly");

            Console.WriteLine("NexVerse runtime secret loading regression: OK");
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXVERSE_ENV_FILE", oldFile);
            Environment.SetEnvironmentVariable("NEXVERSE_DB_PASSWORD", oldPassword);
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
            }
        }
    }
}
