using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

// One containment primitive for native UIA and window-close scenarios.
internal static class WpfScenarioIsolation
{
    internal static async Task RunAsync(
        Type testClass,
        string kind,
        string methodName,
        Action<IsolationStageWriter> childBody)
    {
        const string kindEnvironment = "ALBUMEN_SCENARIO_ISOLATION_KIND";
        const string nonceEnvironment = "ALBUMEN_SCENARIO_ISOLATION_NONCE";
        const string parentIdEnvironment = "ALBUMEN_SCENARIO_ISOLATION_PARENT_ID";
        const string parentStartEnvironment = "ALBUMEN_SCENARIO_ISOLATION_PARENT_START";
        var childKind = Environment.GetEnvironmentVariable(kindEnvironment);
        var nonceText = Environment.GetEnvironmentVariable(nonceEnvironment);
        var parentIdText = Environment.GetEnvironmentVariable(parentIdEnvironment);
        var parentStartText = Environment.GetEnvironmentVariable(parentStartEnvironment);
        if (childKind is not null)
        {
            Assert.Equal(kind, childKind);
            Assert.True(Guid.TryParseExact(nonceText, "N", out var childNonce) && childNonce != Guid.Empty,
                "The isolated child nonce was missing or invalid.");
            Assert.True(int.TryParse(parentIdText, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var parentId) && parentId > 0
                && parentId != Environment.ProcessId, "The isolated child parent process ID was invalid.");
            Assert.True(long.TryParse(parentStartText, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var parentStart),
                "The isolated child parent start time was invalid.");
            using (var parent = Process.GetProcessById(parentId))
            {
                Assert.False(parent.HasExited, "The isolated child's parent process was no longer alive.");
                Assert.Equal(parentStart, parent.StartTime.ToUniversalTime().Ticks);
            }
            using var stage = new IsolationStageWriter(kind, childNonce);
            stage.Entered();
            childBody(stage);
            return;
        }

        Assert.Null(nonceText);
        Assert.Null(parentIdText);
        Assert.Null(parentStartText);
        var assemblyPath = testClass.Assembly.Location;
        var assemblyDirectory = Path.GetDirectoryName(assemblyPath);
        Assert.NotNull(assemblyDirectory);
        var executablePath = Path.ChangeExtension(assemblyPath, ".exe");
        Assert.NotNull(executablePath);
        Assert.Equal("Albumen.Scenarios.Tests.exe", Path.GetFileName(executablePath));
        Assert.Equal(assemblyDirectory, Path.GetDirectoryName(executablePath));
        Assert.True(File.Exists(executablePath),
            $"The xUnit v3 scenario apphost was not found beside its assembly: {executablePath}");
        var nonce = Guid.NewGuid();
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = assemblyDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-automated");
        start.ArgumentList.Add("async");
        start.ArgumentList.Add("-parallel");
        start.ArgumentList.Add("none");
        start.ArgumentList.Add("-noLogo");
        start.ArgumentList.Add("-noColor");
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{testClass.FullName}.{methodName}");
        start.Environment[kindEnvironment] = kind;
        start.Environment[nonceEnvironment] = nonce.ToString("N");
        start.Environment[parentIdEnvironment] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        start.Environment[parentStartEnvironment] =
            current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);

        using var child = new Process { StartInfo = start };
        Assert.True(child.Start(), "The isolated xUnit child process did not start.");
        var childId = child.Id;
        var lifecycleStage = "launched";
        try
        {
            lifecycleStage = "draining-output";
            var standardOutput = child.StandardOutput.ReadToEndAsync();
            var standardError = child.StandardError.ReadToEndAsync();
            var exit = child.WaitForExitAsync();
            var timedOut = false;
            lifecycleStage = "waiting-for-exit";
            try
            {
                await exit.WaitAsync(TimeSpan.FromSeconds(25));
            }
            catch (TimeoutException)
            {
                timedOut = true;
                await ContainIsolatedChildOrFailFastAsync(child, childId, kind, "deadline");
            }

            lifecycleStage = "collecting-output";
            ExceptionDispatchInfo? drainFailure = null;
            string? output = null;
            string? errorOutput = null;
            try
            {
                await Task.WhenAll(standardOutput, standardError).WaitAsync(TimeSpan.FromSeconds(5));
                output = await standardOutput;
                errorOutput = await standardError;
            }
            catch (Exception error)
            {
                drainFailure = ExceptionDispatchInfo.Capture(error);
            }
            output ??= "<standard output did not drain>";
            errorOutput ??= "<standard error did not drain>";
            var hasExited = child.HasExited;
            var diagnostic = $"Isolated child kind={kind}; pid={childId}; timedOut={timedOut}; "
                + $"hasExited={hasExited}; exitCode={(hasExited ? child.ExitCode : -1)}."
                + Environment.NewLine + "STDOUT:" + Environment.NewLine + output
                + Environment.NewLine + "STDERR:" + Environment.NewLine + errorOutput;
            if (drainFailure is not null)
                throw new InvalidOperationException(diagnostic, drainFailure.SourceException);
            Assert.True(child.HasExited, diagnostic);
            Assert.False(timedOut, diagnostic);
            var entered = IsolationStageWriter.Format(kind, nonce, "entered");
            var cleanup = IsolationStageWriter.Format(kind, nonce, "cleanup-ok");
            var stageLines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith(IsolationStageWriter.Prefix, StringComparison.Ordinal)).ToArray();
            Assert.True(stageLines.SequenceEqual([entered, cleanup]),
                $"Expected exactly the entered and cleanup-ok witnesses, in order. Actual witnesses: {string.Join(" | ", stageLines)}."
                    + Environment.NewLine + diagnostic);
            Assert.True(child.ExitCode == 0, diagnostic);
            lifecycleStage = "complete";
        }
        finally
        {
            await ContainIsolatedChildOrFailFastAsync(child, childId, kind, lifecycleStage);
        }
    }

    private static async Task ContainIsolatedChildOrFailFastAsync(
        Process child,
        int childId,
        string kind,
        string stage)
    {
        Exception? containmentFailure = null;
        var exited = false;
        try
        {
            exited = child.HasExited;
        }
        catch (Exception error)
        {
            containmentFailure = error;
        }

        if (!exited)
        {
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (Exception error)
            {
                containmentFailure ??= error;
            }

            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                exited = child.HasExited;
            }
            catch (Exception error)
            {
                containmentFailure ??= error;
                exited = false;
            }
        }

        if (!exited)
        {
            var message = $"Albumen isolated child containment failed; kind={kind}; "
                + $"pid={childId}; stage={stage}.";
            Environment.FailFast(message, containmentFailure);
        }
    }

    internal sealed class IsolationStageWriter(string kind, Guid nonce) : IDisposable
    {
        internal const string Prefix = "ALBUMEN_SCENARIO_CHILD ";
        private readonly TextWriter output = new StreamWriter(
            Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        private int cleanupWritten;

        internal static string Format(string kind, Guid nonce, string stage) =>
            $"{Prefix}kind={kind} nonce={nonce:N} stage={stage}";

        internal void Entered() => Write("entered");

        internal void CleanupSucceeded()
        {
            Assert.Equal(0, Interlocked.Exchange(ref cleanupWritten, 1));
            Write("cleanup-ok");
        }

        private void Write(string stage)
        {
            output.WriteLine(Format(kind, nonce, stage));
            output.Flush();
        }

        public void Dispose() => output.Dispose();
    }

}
