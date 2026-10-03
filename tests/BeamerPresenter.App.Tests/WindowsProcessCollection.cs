namespace BeamerPresenter.App.Tests;

// Process startup and Windows policy queries share limited resources on hosted runners.
[CollectionDefinition("Windows process tests", DisableParallelization = true)]
public sealed class WindowsProcessCollection;
