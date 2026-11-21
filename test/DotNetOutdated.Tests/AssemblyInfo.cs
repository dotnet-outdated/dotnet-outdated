using Xunit;

// The end-to-end tests run the tool in-process, so its console output would otherwise go straight to the
// test run's output. Capture it into each test's output instead, where it shows alongside a failure.
[assembly: CaptureConsole]
