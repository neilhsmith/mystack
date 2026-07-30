using MyStack.Worker.Tests;

// One Postgres container, one RabbitMQ container and one host for the whole assembly. Starting
// containers per class would dominate the suite's runtime long before there are enough tests to
// justify it.
[assembly: AssemblyFixture(typeof(WorkerAppFixture))]
