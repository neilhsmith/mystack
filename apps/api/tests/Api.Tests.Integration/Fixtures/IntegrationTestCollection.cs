namespace Api.Tests.Integration.Fixtures;

[CollectionDefinition(nameof(IntegrationTestCollection))]
public sealed class IntegrationTestCollection : ICollectionFixture<ApiTestFactory>;
