
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace LightweightDI
{
    public enum ServiceLifetime
    {
        Transient,
        Singleton
    }

    public class ServiceDescriptor
    {
        public Type ServiceType { get; }
        public Type ImplementationType { get; }
        public ServiceLifetime Lifetime { get; }
        internal object? ImplementationInstance { get; set; }

        public ServiceDescriptor(Type serviceType, Type implementationType, ServiceLifetime lifetime)
        {
            ServiceType = serviceType;
            ImplementationType = implementationType;
            Lifetime = lifetime;
        }
    }

    public class Container
    {
        private readonly ConcurrentDictionary<Type, ServiceDescriptor> _descriptors = new();
        private readonly object _singletonLock = new();

        public void RegisterTransient<TService, TImplementation>() where TImplementation : TService
        {
            Register(typeof(TService), typeof(TImplementation), ServiceLifetime.Transient);
        }

        public void RegisterSingleton<TService, TImplementation>() where TImplementation : TService
        {
            Register(typeof(TService), typeof(TImplementation), ServiceLifetime.Singleton);
        }

        public void RegisterInstance<TService>(TService instance)
        {
            var desc = new ServiceDescriptor(typeof(TService), instance!.GetType(), ServiceLifetime.Singleton)
            {
                ImplementationInstance = instance
            };
            _descriptors[typeof(TService)] = desc;
        }

        public void Register(Type serviceType, Type implementationType, ServiceLifetime lifetime)
        {
            if (!serviceType.IsAssignableFrom(implementationType) && !serviceType.IsGenericTypeDefinition)
                throw new ArgumentException($"{implementationType} does not implement {serviceType}");

            var desc = new ServiceDescriptor(serviceType, implementationType, lifetime);
            _descriptors[serviceType] = desc;
        }

        public T Resolve<T>() => (T)Resolve(typeof(T));

        public object Resolve(Type serviceType)
        {
            if (_descriptors.TryGetValue(serviceType, out var descriptor))
            {
                if (descriptor.Lifetime == ServiceLifetime.Singleton)
                {
                    if (descriptor.ImplementationInstance != null) return descriptor.ImplementationInstance;

                    lock (_singletonLock)
                    {
                        if (descriptor.ImplementationInstance == null)
                        {
                            descriptor.ImplementationInstance = CreateInstance(descriptor.ImplementationType);
                        }
                    }

                    return descriptor.ImplementationInstance!;
                }

                // Transient
                return CreateInstance(descriptor.ImplementationType);
            }

            // Not registered - if concrete type, try to create
            if (!serviceType.IsAbstract && !serviceType.IsInterface)
            {
                return CreateInstance(serviceType);
            }

            throw new InvalidOperationException($"Service of type {serviceType} is not registered");
        }

        private object CreateInstance(Type implementationType)
        {
            // Handle parameterless
            var constructors = implementationType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (constructors.Length == 0)
                throw new InvalidOperationException($"No public constructors for {implementationType}");

            // choose constructor with maximum parameters (simple heuristic)
            var ctor = constructors.OrderByDescending(c => c.GetParameters().Length).First();
            var parameters = ctor.GetParameters();
            if (parameters.Length == 0)
            {
                return Activator.CreateInstance(implementationType)!;
            }

            var parameterInstances = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;
                parameterInstances[i] = Resolve(paramType);
            }

            return ctor.Invoke(parameterInstances)!;
        }
    }

    // Example interfaces and implementations
    public interface IEmailSender { string Send(string to, string body); }
    public class AzureEmailSender : IEmailSender
    {
        private readonly ITemplateRenderer _renderer;
        public AzureEmailSender(ITemplateRenderer renderer)
        {
            _renderer = renderer;
        }
        public string Send(string to, string body)
        {
            return $"Azure: to={to}; body={_renderer.Render(body)}";
        }
    }

    public interface ITemplateRenderer { string Render(string template); }
    public class SimpleTemplateRenderer : ITemplateRenderer
    {
        public string Render(string template) => template?.ToUpperInvariant() ?? string.Empty;
    }

    // Unit tests using xUnit
    public class ContainerTests
    {
        [Fact]
        public void TransientRegistration_ReturnsDifferentInstances()
        {
            var c = new Container();
            c.RegisterTransient<ITemplateRenderer, SimpleTemplateRenderer>();

            var r1 = c.Resolve<ITemplateRenderer>();
            var r2 = c.Resolve<ITemplateRenderer>();

            Assert.NotNull(r1);
            Assert.NotNull(r2);
            Assert.IsType<SimpleTemplateRenderer>(r1);
            Assert.IsType<SimpleTemplateRenderer>(r2);
            Assert.NotSame(r1, r2);
        }

        [Fact]
        public void SingletonRegistration_ReturnsSameInstance()
        {
            var c = new Container();
            c.RegisterSingleton<ITemplateRenderer, SimpleTemplateRenderer>();

            var r1 = c.Resolve<ITemplateRenderer>();
            var r2 = c.Resolve<ITemplateRenderer>();

            Assert.Same(r1, r2);
        }

        [Fact]
        public void NestedDependencies_AreResolved()
        {
            var c = new Container();
            c.RegisterSingleton<ITemplateRenderer, SimpleTemplateRenderer>();
            c.RegisterTransient<IEmailSender, AzureEmailSender>();

            var sender = c.Resolve<IEmailSender>();
            Assert.NotNull(sender);
            var result = sender.Send("user@example.com", "hello");
            Assert.Contains("Azure:", result);
            Assert.Contains("HELLO", result); // rendered to upper-case by SimpleTemplateRenderer
        }

        [Fact]
        public void ResolveConcreteWithoutRegistration_Works()
        {
            var c = new Container();
            // Concrete type with parameterless ctor
            var renderer = c.Resolve<SimpleTemplateRenderer>();
            Assert.IsType<SimpleTemplateRenderer>(renderer);
        }

        [Fact]
        public void RegisterInstance_UsesGivenInstance()
        {
            var c = new Container();
            var instance = new SimpleTemplateRenderer();
            c.RegisterInstance<ITemplateRenderer>(instance);

            var r = c.Resolve<ITemplateRenderer>();
            Assert.Same(instance, r);
        }

        [Fact]
        public void UnregisteredInterface_Throws()
        {
            var c = new Container();
            Assert.Throws<InvalidOperationException>(() => c.Resolve<IEmailSender>());
        }
    }
}
