// This executable is only the isolated native/manual harness. Automated tests use dotnet test.
if (args is ["--local-pipeline-harness" or "--local-pipeline-lifecycle", var modelRoot, var evidenceRoot])
{
    if (Environment.GetEnvironmentVariable("GAMELINGOAI_RUN_LOCAL_PIPELINE") != "1")
    {
        Console.Error.WriteLine("Explicit permission to capture the owned synthetic window is required.");
        return 2;
    }
    try
    {
        await LocalPipelineHarness.RunAsync(modelRoot, evidenceRoot, args[0] == "--local-pipeline-lifecycle");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("Owned local pipeline failed: " + error.GetType().Name);
        return 1;
    }
}
if (args is ["--overlay-harness"])
{
    try
    {
        await OverlayWindowTests.RunAsync();
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("Owned overlay harness failed: " + error.GetType().Name);
        return 1;
    }
}
Console.Error.WriteLine("Use dotnet test for automated tests, or --overlay-harness for the isolated native harness.");
return 2;
