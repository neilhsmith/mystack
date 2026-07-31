using MyStack.Auth.Tests;

// One Postgres container and one host for the whole assembly. Starting a container per class
// would dominate the suite's runtime long before there are enough tests to justify it.
[assembly: AssemblyFixture(typeof(AuthAppFixture))]
// Serial: the seeding tests boot temporary hosts that listen on the same broker queue as the
// shared host, and a parallel MessagingTests could have its message consumed by the wrong one.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
