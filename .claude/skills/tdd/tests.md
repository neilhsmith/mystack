# Good and Bad Tests

## Good Tests

**Integration-style**: Test through real interfaces, not mocks of internal parts.

```csharp
// GOOD: Tests observable behavior
[Fact]
public async Task User_can_checkout_with_valid_cart()
{
    var cart = CreateCart();
    cart.Add(product);

    var result = await Checkout(cart, paymentMethod);

    Assert.Equal("confirmed", result.Status);
}
```

Characteristics:

- Tests behavior users/callers care about
- Uses public API only
- Survives internal refactors
- Describes WHAT, not HOW
- One logical assertion per test

## Bad Tests

**Implementation-detail tests**: Coupled to internal structure.

```csharp
// BAD: Tests implementation details
[Fact]
public async Task Checkout_calls_PaymentService_Process()
{
    var mockPayment = new Mock<IPaymentService>();
    await Checkout(cart, payment);
    mockPayment.Verify(p => p.Process(cart.Total), Times.Once);
}
```

Red flags:

- Mocking internal collaborators
- Testing private methods
- Asserting on call counts/order
- Test breaks when refactoring without behavior change
- Test name describes HOW not WHAT
- Verifying through external means instead of interface

```csharp
// BAD: Bypasses interface to verify
[Fact]
public async Task CreateUser_saves_to_database()
{
    await CreateUser(new UserDto { Name = "Alice" });
    var row = await db.QuerySingleAsync<User>(
        "SELECT * FROM users WHERE name = @name",
        new { name = "Alice" });
    Assert.NotNull(row);
}

// GOOD: Verifies through interface
[Fact]
public async Task CreateUser_makes_user_retrievable()
{
    var user = await CreateUser(new UserDto { Name = "Alice" });

    var retrieved = await GetUser(user.Id);

    Assert.Equal("Alice", retrieved.Name);
}
```
