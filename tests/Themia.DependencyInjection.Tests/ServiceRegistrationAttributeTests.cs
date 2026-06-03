using Microsoft.Extensions.DependencyInjection;
using Themia.DependencyInjection;
using Xunit;

namespace Themia.DependencyInjection.Tests;

public class ServiceRegistrationAttributeTests
{
    [Fact]
    public void ScopedAttribute_Lifetime_IsScoped()
    {
        var attr = new ScopedAttribute();
        Assert.Equal(ServiceLifetime.Scoped, attr.Lifetime);
    }

    [Fact]
    public void SingletonAttribute_Lifetime_IsSingleton()
    {
        var attr = new SingletonAttribute();
        Assert.Equal(ServiceLifetime.Singleton, attr.Lifetime);
    }

    [Fact]
    public void TransientAttribute_Lifetime_IsTransient()
    {
        var attr = new TransientAttribute();
        Assert.Equal(ServiceLifetime.Transient, attr.Lifetime);
    }

    [Fact]
    public void ScopedAttribute_ImplementsIServiceRegistrationAttribute()
    {
        var attr = new ScopedAttribute();
        Assert.IsAssignableFrom<IServiceRegistrationAttribute>(attr);
    }

    [Fact]
    public void SingletonAttribute_ImplementsIServiceRegistrationAttribute()
    {
        var attr = new SingletonAttribute();
        Assert.IsAssignableFrom<IServiceRegistrationAttribute>(attr);
    }

    [Fact]
    public void TransientAttribute_ImplementsIServiceRegistrationAttribute()
    {
        var attr = new TransientAttribute();
        Assert.IsAssignableFrom<IServiceRegistrationAttribute>(attr);
    }

    [Fact]
    public void ScopedAttribute_DefaultValues_AreExpected()
    {
        var attr = new ScopedAttribute();
        Assert.Null(attr.ServiceType);
        Assert.Null(attr.ServiceKey);
        Assert.False(attr.AllowSelfRegistration);
    }

    [Fact]
    public void ScopedAttribute_ServiceType_CanBeSet()
    {
        var attr = new ScopedAttribute { ServiceType = typeof(IDisposable) };
        Assert.Equal(typeof(IDisposable), attr.ServiceType);
    }

    [Fact]
    public void AttributesAreInThemiaDependencyInjectionNamespace()
    {
        Assert.Equal("Themia.DependencyInjection", typeof(ScopedAttribute).Namespace);
        Assert.Equal("Themia.DependencyInjection", typeof(SingletonAttribute).Namespace);
        Assert.Equal("Themia.DependencyInjection", typeof(TransientAttribute).Namespace);
    }
}
