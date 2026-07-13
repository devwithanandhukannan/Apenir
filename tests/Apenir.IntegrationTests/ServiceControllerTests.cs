using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Moq;
using Xunit;
using FluentAssertions;
using Apenir.API.Controllers;
using Apenir.Core.Entities;
using Apenir.Core.Interfaces;
using Apenir.Application.Common.Models;

namespace Apenir.IntegrationTests;

public class ServiceControllerTests
{
    [Fact]
    public async Task AddService_ShouldReturnOk_WhenRequestIsValid()
    {
        // Arrange
        var mockSet = new Mock<DbSet<Service>>();
        var mockContext = new Mock<IApplicationDbContext>();
        mockContext.Setup(c => c.Services).Returns(mockSet.Object);

        var controller = new ServiceController(mockContext.Object);
        var request = new CreateServiceRequest("Test Service", "Description", "Category", 100.00m, 10.00m);

        // Act
        var result = await controller.AddService(request);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.Value.Should().BeOfType<ApiResponse<Service>>();
        
        var apiResponse = (ApiResponse<Service>)okResult.Value!;
        apiResponse.Success.Should().BeTrue();
        apiResponse.Message.Should().Be("SERVICE_ADDED");
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.Name.Should().Be("Test Service");
        apiResponse.Data!.BasePrice.Should().Be(100.00m);

        mockSet.Verify(m => m.Add(It.IsAny<Service>()), Times.Once());
        mockContext.Verify(m => m.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task AddService_ShouldReturnBadRequest_WhenNameIsEmpty()
    {
        // Arrange
        var mockContext = new Mock<IApplicationDbContext>();
        var controller = new ServiceController(mockContext.Object);
        var request = new CreateServiceRequest("", "Description", "Category", 100.00m, 10.00m);

        // Act
        var result = await controller.AddService(request);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetServices_ShouldReturnOk_WithActiveServices()
    {
        // Arrange
        var services = new List<Service>
        {
            new() { Name = "Active Service 1", IsActive = true, Category = "Hematology", BasePrice = 50.00m },
            new() { Name = "Inactive Service", IsActive = false, Category = "Biochemistry", BasePrice = 75.00m }
        }.AsQueryable();

        var mockSet = new Mock<DbSet<Service>>();
        mockSet.As<IAsyncEnumerable<Service>>()
            .Setup(d => d.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(new TestDbAsyncEnumerator<Service>(services.GetEnumerator()));

        mockSet.As<IQueryable<Service>>()
            .Setup(m => m.Provider)
            .Returns(new TestDbAsyncQueryProvider<Service>(services.Provider));

        mockSet.As<IQueryable<Service>>().Setup(m => m.Expression).Returns(services.Expression);
        mockSet.As<IQueryable<Service>>().Setup(m => m.ElementType).Returns(services.ElementType);
        mockSet.As<IQueryable<Service>>().Setup(m => m.GetEnumerator()).Returns(services.GetEnumerator());

        var mockContext = new Mock<IApplicationDbContext>();
        mockContext.Setup(c => c.Services).Returns(mockSet.Object);

        var controller = new ServiceController(mockContext.Object);

        // Act
        var result = await controller.GetServices();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.Value.Should().BeOfType<ApiResponse<List<Service>>>();
        
        var apiResponse = (ApiResponse<List<Service>>)okResult.Value!;
        apiResponse.Success.Should().BeTrue();
        apiResponse.Message.Should().Be("SERVICES_RETRIEVED");
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data.Should().HaveCount(1);
        apiResponse.Data!.First().Name.Should().Be("Active Service 1");
    }
}

// --- EF Core Async Query Mocking Boilerplate Helpers ---

public class TestDbAsyncQueryProvider<TEntity> : IAsyncQueryProvider
{
    private readonly IQueryProvider _inner;

    public TestDbAsyncQueryProvider(IQueryProvider inner)
    {
        _inner = inner;
    }

    public IQueryable CreateQuery(Expression expression)
    {
        return new TestDbAsyncEnumerable<TEntity>(expression);
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        return new TestDbAsyncEnumerable<TElement>(expression);
    }

    public object? Execute(Expression expression)
    {
        return _inner.Execute(expression);
    }

    public TResult Execute<TResult>(Expression expression)
    {
        return _inner.Execute<TResult>(expression);
    }

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var expectedResultType = typeof(TResult).GetGenericArguments()[0];
        var executionResult = typeof(IQueryProvider)
            .GetMethods()
            .First(method => method.Name == nameof(IQueryProvider.Execute) && method.IsGenericMethod)
            .MakeGenericMethod(expectedResultType)
            .Invoke(this, new object[] { expression });

        return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(expectedResultType)
            .Invoke(null, new[] { executionResult })!;
    }
}

public class TestDbAsyncEnumerable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
{
    public TestDbAsyncEnumerable(IEnumerable<T> enumerable) : base(enumerable)
    { }

    public TestDbAsyncEnumerable(Expression expression) : base(expression)
    { }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return new TestDbAsyncEnumerator<T>(this.AsEnumerable().GetEnumerator());
    }

    IQueryProvider IQueryable.Provider => new TestDbAsyncQueryProvider<T>(this);
}

public class TestDbAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly IEnumerator<T> _inner;

    public TestDbAsyncEnumerator(IEnumerator<T> inner)
    {
        _inner = inner;
    }

    public T Current => _inner.Current;

    public ValueTask DisposeAsync()
    {
        _inner.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> MoveNextAsync()
    {
        return ValueTask.FromResult(_inner.MoveNext());
    }
}
