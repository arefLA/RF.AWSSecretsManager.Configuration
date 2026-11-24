using System.Runtime.CompilerServices;

// Lets the test project reach internal members (for example to inject a client factory into the source).
[assembly: InternalsVisibleTo("RF.AWSSecretsManager.Configuration.Tests")]
