using MyStack.Auth.Tests;

// One Postgres container and one host for the whole assembly. Starting a container per class
// would dominate the suite's runtime long before there are enough tests to justify it.
[assembly: AssemblyFixture(typeof(AuthAppFixture))]
