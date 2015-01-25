﻿/*
The MIT License (MIT)

Copyright (c) 2013 Maksim Volkau

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

namespace DryIoc
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Text;
    using System.Threading;

    /// <summary>IoC Container. Documentation is available at https://bitbucket.org/dadhi/dryioc. </summary>
    public sealed partial class Container : IContainer
    {
        /// <summary>Creates new container, optionally providing <see cref="Rules"/> to modify default container behavior.</summary>
        /// <param name="rules">(optional) Rules to modify container default resolution behavior. 
        /// If not specified, then <see cref="DryIoc.Rules.Default"/> will be used.</param>
        /// <param name="scopeContext">(optional) Scope context to use for <see cref="Reuse.InCurrentScope"/>, default is <see cref="ThreadScopeContext"/>.</param>
        public Container(Rules rules = null, IScopeContext scopeContext = null)
            : this(rules ?? Rules.Default,
            Ref.Of(HashTree<Type, object>.Empty),
            Ref.Of(HashTree<Type, Factory[]>.Empty),
            Ref.Of(WrappersSupport.Wrappers),
            new Scope(), scopeContext) { }

        /// <summary>Creates new container with configured rules.</summary>
        /// <param name="configure">Delegate gets <see cref="DryIoc.Rules.Default"/> as input and may return configured rules.</param>
        /// <param name="scopeContext">(optional) Scope context to use for <see cref="Reuse.InCurrentScope"/>, default is <see cref="ThreadScopeContext"/>.</param>
        public Container(Func<Rules, Rules> configure, IScopeContext scopeContext = null)
            : this(configure.ThrowIfNull()(Rules.Default) ?? Rules.Default, scopeContext) { }

        /// <summary>Shares all of container state except Cache and specifies new rules.</summary>
        /// <param name="configure">(optional) Configure rules, if not specified then uses Rules from current container.</param> 
        /// <param name="scopeContext">(optional) New scope context, if not specified then uses context from current container.</param>
        /// <returns>New container.</returns>
        public IContainer With(Func<Rules, Rules> configure = null, IScopeContext scopeContext = null)
        {
            ThrowIfContainerDisposed();
            var rules = configure == null ? Rules : configure(Rules);
            scopeContext = scopeContext ?? _scopeContext;
            return new Container(rules, _factories, _decorators, _wrappers, _singletonScope, scopeContext, _openedScope, _disposed);
        }

        /// <summary>Returns new container with all expression, delegate, items cache removed/reset.
        /// It will preserve resolved services in Singleton/Current scope.</summary>
        /// <returns>New container with empty cache.</returns>
        public IContainer WithoutCache()
        {
            ThrowIfContainerDisposed();
            return new Container(Rules,
                _factories, _decorators, _wrappers, _singletonScope, _scopeContext, _openedScope, _disposed /*drop cache*/);
        }

        /// <summary>Creates new container with state shared with original except singletons and cache.
        /// Dropping cache is required because singletons are cached in resolution state.</summary>
        /// <returns>New container with empty Singleton Scope.</returns>
        public IContainer WithoutSingletonsAndCache()
        {
            ThrowIfContainerDisposed();
            return new Container(Rules,
                _factories, _decorators, _wrappers, null/*singletonScope*/, _scopeContext, _openedScope, _disposed /*drop cache*/);
        }

        /// <summary>Shares all parts with original container But copies registration, so the new registration
        /// won't be visible in original. Registrations include decorators and wrappers as well.</summary>
        /// <param name="preserveCache">(optional) If set preserves cache if you know what to do.</param>
        /// <returns>New container with copy of all registrations.</returns>
        public IContainer WithRegistrationsCopy(bool preserveCache = false)
        {
            ThrowIfContainerDisposed();
            return preserveCache
                ? new Container(Rules, Ref.Of(_factories.Value), Ref.Of(_decorators.Value), Ref.Of(_wrappers.Value),
                    _singletonScope, _scopeContext, _openedScope, _disposed)
                : new Container(Rules, Ref.Of(_factories.Value), Ref.Of(_decorators.Value), Ref.Of(_wrappers.Value),
                    _singletonScope, _scopeContext, _openedScope, _disposed,
                    _defaultFactoryDelegatesCache, _keyedFactoryDelegatesCache, _resolutionStateCache);
        }

        /// <summary>Creates new container with new opened scope and set this scope as current in provided/inherited context.</summary>
        /// <param name="scopeName">(optional) Name for opened scope to allow reuse to identify the scope.</param>
        /// <param name="configure">(optional) Configure rules, if not specified then uses Rules from current container.</param> 
        /// <returns>New container with different current scope.</returns>
        /// <example><code lang="cs"><![CDATA[
        /// using (var scoped = container.OpenScope())
        /// {
        ///     var handler = scoped.Resolve<IHandler>();
        ///     handler.Handle(data);
        /// }
        /// ]]></code></example>
        public IContainer OpenScope(object scopeName = null, Func<Rules, Rules> configure = null)
        {
            ThrowIfContainerDisposed();

            scopeName = scopeName ?? (_openedScope == null ? _scopeContext.RootScopeName : null);
            var nestedOpenedScope = new Scope(_openedScope, scopeName);

            // Replacing current context scope with new nested only if current is the same as nested parent, otherwise throw.
            _scopeContext.SetCurrent(scope =>
                nestedOpenedScope.ThrowIf(scope != _openedScope, Error.NOT_DIRECT_SCOPE_PARENT));

            var rules = configure == null ? Rules : configure(Rules);
            return new Container(rules,
                _factories, _decorators, _wrappers, _singletonScope, _scopeContext, nestedOpenedScope,
                _disposed, _defaultFactoryDelegatesCache, _keyedFactoryDelegatesCache, _resolutionStateCache);
        }

        /// <summary>Creates child container using the same rules as its created from.
        /// Additionally child container will fallback for not registered service to it parent.</summary>
        /// <param name="shareSingletons">If set allow to share singletons from parent container.</param>
        /// <returns>New child container.</returns>
        public IContainer CreateChildContainer(bool shareSingletons = false)
        {
            ThrowIfContainerDisposed();
            var newRules = Rules.WithUnknownServiceResolver(ResolveFromParents(this));
            return !shareSingletons
                ? new Container(newRules)
                : new Container(newRules,
                    Ref.Of(HashTree<Type, object>.Empty), Ref.Of(HashTree<Type, Factory[]>.Empty), Ref.Of(WrappersSupport.Wrappers),
                    _singletonScope);
        }

        /// <summary>The rule to fallback to multiple parent containers in order to resolve service unresolved in rule owner.</summary>
        /// <param name="parents">One or many container to resolve from if service is not resolved from original container.</param>
        /// <returns>New rule with fallback to resolve service from specified parent containers.</returns>
        /// <remarks>There are two options to detach parents link: 1st - save original container before linking to parents and use it; 
        /// 2nd - save rule returned by this method and then remove saved rule from container using <see cref="DryIoc.Rules.WithoutUnknownServiceResolver"/>.</remarks>
        public static Rules.UnknownServiceResolver ResolveFromParents(params IContainer[] parents)
        {
            var parentWeakRefs = parents.ThrowIf(ArrayTools.IsNullOrEmpty).Select(p => p.ContainerWeakRef).ToArray();
            return request =>
            {
                Factory factory = null;
                for (var i = 0; i < parentWeakRefs.Length && factory == null; i++)
                {
                    var parentWeakRef = parentWeakRefs[i];
                    var parentRequest = request.SwitchContainer(parentWeakRef);

                    // Enable continue to  next container if factory is not found in first;
                    if (parentRequest.IfUnresolved != IfUnresolved.ReturnDefault)
                        parentRequest = parentRequest.UpdateServiceInfo(info =>
                            ServiceInfo.Of(info.ServiceType, ifUnresolved: IfUnresolved.ReturnDefault).InheritInfo(info));

                    var parent = parentWeakRef.GetTarget();
                    factory = parent.ResolveFactory(parentRequest);
                }

                return factory == null ? null : new ExpressionFactory(r => factory.GetExpressionOrDefault(r));
            };
        }

        /// <summary>Disposes container current scope and that means container itself.</summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            if (_openedScope != null)
            {
                var openedScope = _openedScope;
                _scopeContext.SetCurrent(scope =>
                    scope.ThrowIf(scope != openedScope, Error.UNABLE_TO_DISPOSE_NOT_A_CURRENT_SCOPE).Parent);

                _openedScope.Dispose();
                _openedScope = null;
                return; // Skip the rest for scoped container
            }

            if (_scopeContext is IDisposable)
                ((IDisposable)_scopeContext).Dispose();
            _scopeContext = null;

            _singletonScope.Dispose();
            _singletonScope = null;

            _factories.Swap(_ => HashTree<Type, object>.Empty);
            _decorators.Swap(_ => HashTree<Type, Factory[]>.Empty);
            _wrappers.Swap(_ => HashTree<Type, Factory>.Empty);

            _defaultFactoryDelegatesCache = Ref.Of(HashTree<Type, FactoryDelegate>.Empty);
            _keyedFactoryDelegatesCache = Ref.Of(HashTree<Type, HashTree<object, FactoryDelegate>>.Empty);

            _resolutionStateCache.Dispose();
            _resolutionStateCache = null;

            _containerWeakRef = null;

            Rules = Rules.Empty;
        }

        #region Static state

        internal static readonly ParameterExpression StateParamExpr = Expression.Parameter(typeof(AppendableArray), "state");

        internal static readonly ParameterExpression ResolverProviderParamExpr = Expression.Parameter(typeof(IResolverProvider), "r");
        internal static readonly Expression ResolverExpr = Expression.Property(ResolverProviderParamExpr, "Resolver");

        internal static readonly ParameterExpression ResolutionScopeParamExpr = Expression.Parameter(typeof(IScope), "scope");

        #endregion

        #region IRegistrator

        /// <summary>Stores factory into container using <paramref name="serviceType"/> and <paramref name="serviceKey"/> as key
        /// for later lookup.</summary>
        /// <param name="factory">Any subtypes of <see cref="Factory"/>.</param>
        /// <param name="serviceType">Type of service to resolve later.</param>
        /// <param name="serviceKey">(optional) Service key of any type with <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>
        /// implemented.</param>
        /// <param name="ifAlreadyRegistered">(optional) Says how to handle existing registration with the same 
        /// <paramref name="serviceType"/> and <paramref name="serviceKey"/>.</param>
        public void Register(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered)
        {
            ThrowIfContainerDisposed();
            factory.ThrowIfNull().BeforeRegistrationCheck(this, serviceType.ThrowIfNull(), serviceKey);

            var handler = Rules.BeforeFactoryRegistrationHook;
            if (handler != null)
                handler(factory, serviceType, serviceKey);

            switch (factory.FactoryType)
            {
                case FactoryType.Decorator:
                    _decorators.Swap(x => x.AddOrUpdate(serviceType, new[] { factory }, ArrayTools.Append)); break;
                case FactoryType.Wrapper:
                    _wrappers.Swap(x => x.AddOrUpdate(serviceType, factory)); break;
                default:
                    AddOrUpdateServiceFactory(factory, serviceType, serviceKey, ifAlreadyRegistered); break;
            }
        }

        /// <summary>Returns true if there is registered factory for requested service type and key, 
        /// and factory is of specified factory type and condition.</summary>
        /// <param name="serviceType">Service type to look for.</param>
        /// <param name="serviceKey">Service key to look for.</param>
        /// <param name="factoryType">Expected registered factory type.</param>
        /// <param name="condition">Expected factory condition.</param>
        /// <returns>Returns true if factory requested is registered.</returns>
        public bool IsRegistered(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition)
        {
            ThrowIfContainerDisposed();
            serviceType = serviceType.ThrowIfNull();
            switch (factoryType)
            {
                case FactoryType.Wrapper:
                    var wrapper = ((IContainer)this).GetWrapperFactoryOrDefault(serviceType);
                    return wrapper != null && (condition == null || condition(wrapper));

                case FactoryType.Decorator:
                    var decorators = _decorators.Value.GetValueOrDefault(serviceType);
                    return decorators != null && decorators.Length != 0 && (condition == null || decorators.Any(condition));

                default:
                    Rules.FactorySelectorRule selector = (t, k, factories) => factories.FirstOrDefault(
                        f => f.Key.Equals(k) && (condition == null || condition(f.Value))).Value;

                    var factory = GetServiceFactoryOrDefault(serviceType, serviceKey, selector, retryForOpenGeneric: true);
                    return factory != null;
            }
        }

        /// <summary>Removes specified factory from registry. 
        /// Factory is removed only from registry, if there is relevant cache, it will be kept.
        /// Use <see cref="WithoutCache"/> to remove all the cache.</summary>
        /// <param name="serviceType">Service type to look for.</param>
        /// <param name="serviceKey">Service key to look for.</param>
        /// <param name="factoryType">Expected factory type.</param>
        /// <param name="condition">Expected factory condition.</param>
        public void Unregister(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition)
        {
            ThrowIfContainerDisposed();
            object removed = null; // Factory or Factory[] or FactoriesEntry
            switch (factoryType)
            {
                case FactoryType.Wrapper:
                    _wrappers.Swap(_ => _.Update(serviceType, null, (factory, _null) =>
                    {
                        if (factory != null && condition != null && !condition(factory))
                            return factory;
                        removed = factory;
                        return null;
                    }));
                    break;
                case FactoryType.Decorator:
                    _decorators.Swap(_ => _.Update(serviceType, null, (factories, _null) =>
                    {
                        var remaining = condition == null ? null : factories.Where(f => !condition(f)).ToArray();
                        removed = remaining == null || remaining.Length == 0 ? factories : factories.Except(remaining).ToArray();
                        return remaining;
                    }));
                    break;
                default:
                    removed = UnregisterServiceFactory(serviceType, serviceKey, condition);
                    break;
            }

            if (removed != null)
                UnregisterFactoriesSproutByFactoryProvider(removed, factoryType);
        }

        private object UnregisterServiceFactory(Type serviceType, object serviceKey = null, Func<Factory, bool> condition = null)
        {
            object removed = null;
            if (serviceKey == null && condition == null) // simplest case with simplest handling
                _factories.Swap(_ => _.Update(serviceType, null, (entry, _null) =>
                {
                    removed = entry;
                    return null;
                }));
            else
                _factories.Swap(_ => _.Update(serviceType, null, (entry, _null) =>
                {
                    if (entry == null)
                        return null;

                    if (entry is Factory)
                    {
                        if ((serviceKey != null && !DefaultKey.Value.Equals(serviceKey)) ||
                            (condition != null && !condition((Factory)entry)))
                            return entry; // keep entry
                        removed = entry; // otherwise remove it (the only case if serviceKey == DefaultKey.Value)
                        return null;
                    }

                    var factoriesEntry = (FactoriesEntry)entry;
                    var oldFactories = factoriesEntry.Factories;
                    var remainingFactories = HashTree<object, Factory>.Empty;
                    if (serviceKey == null) // automatically means condition != null
                    {
                        // keep factories for which condition is true
                        foreach (var factory in oldFactories.Enumerate())
                            if (condition != null && !condition(factory.Value))
                                remainingFactories = remainingFactories.AddOrUpdate(factory.Key, factory.Value);
                    }
                    else // serviceKey is not default, which automatically means condition == null
                    {
                        // set to null factory with specified key if its found
                        remainingFactories = oldFactories;
                        var factory = oldFactories.GetValueOrDefault(serviceKey);
                        if (factory != null)
                            remainingFactories = oldFactories.Height > 1
                                ? oldFactories.Update(serviceKey, null)
                                : HashTree<object, Factory>.Empty;
                    }

                    if (remainingFactories.IsEmpty)
                    {
                        // if no more remaining factories, then delete the whole entry
                        removed = entry;
                        return null;
                    }

                    removed = oldFactories.Enumerate().Except(remainingFactories.Enumerate()).Select(f => f.Value).ToArray();

                    if (remainingFactories.Height == 1 && DefaultKey.Value.Equals(remainingFactories.Key))
                        return remainingFactories.Value; // replace entry with single remaining default factory

                    // update last default key if current default key was removed
                    var newDefaultKey = factoriesEntry.LastDefaultKey;
                    if (newDefaultKey != null && remainingFactories.GetValueOrDefault(newDefaultKey) == null)
                        newDefaultKey = remainingFactories.Enumerate().Select(x => x.Key)
                            .OfType<DefaultKey>().OrderByDescending(key => key.RegistrationOrder).FirstOrDefault();
                    return new FactoriesEntry(newDefaultKey, remainingFactories);
                }));
            return removed;
        }

        private void UnregisterFactoriesSproutByFactoryProvider(object removed, FactoryType factoryType)
        {
            if (removed is Factory)
                UnregisterProvidedFactories((Factory)removed, factoryType);
            else if (removed is FactoriesEntry)
                foreach (var f in ((FactoriesEntry)removed).Factories.Enumerate())
                    UnregisterProvidedFactories(f.Value, factoryType);
            else if (removed is Factory[])
                foreach (var f in ((Factory[])removed))
                    UnregisterProvidedFactories(f, factoryType);
        }

        private void UnregisterProvidedFactories(Factory factory, FactoryType factoryType)
        {
            if (factory != null && factory.Provider != null)
                foreach (var f in factory.Provider.ProvidedFactoriesServiceTypeKey)
                    Unregister(f.Key, f.Value, factoryType, null);
        }

        #endregion

        #region IResolver

        object IResolver.ResolveDefault(Type serviceType, IfUnresolved ifUnresolved, Request parentOrEmpty)
        {
            var factoryDelegate = _defaultFactoryDelegatesCache.Value.GetValueOrDefault(serviceType);
            return factoryDelegate != null
                ? factoryDelegate(_resolutionStateCache.Items, _containerWeakRef, null)
                : ResolveAndCacheDefaultDelegate(serviceType, ifUnresolved, parentOrEmpty);
        }

        object IResolver.ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved, Type requiredServiceType,
            Request parentOrEmpty)
        {
            var cacheServiceKey = serviceKey;
            if (requiredServiceType != null)
            {
                var wrappedServiceType = ((IContainer)this).UnwrapServiceType(serviceType)
                    .ThrowIfNotOf(requiredServiceType, Error.WRAPPED_NOT_ASSIGNABLE_FROM_REQUIRED_TYPE, serviceType);

                if (serviceType == wrappedServiceType)
                    serviceType = requiredServiceType;
                else
                    cacheServiceKey = serviceKey == null ? requiredServiceType
                        : (object)new KV<Type, object>(requiredServiceType, serviceKey);
            }

            // If service key is null, then use resolve default instead of keyed.
            if (cacheServiceKey == null)
                return ((IResolver)this).ResolveDefault(serviceType, ifUnresolved, parentOrEmpty);

            ThrowIfContainerDisposed();

            FactoryDelegate factoryDelegate;

            var factoryDelegates = _keyedFactoryDelegatesCache.Value.GetValueOrDefault(serviceType);
            if (factoryDelegates != null &&
                (factoryDelegate = factoryDelegates.GetValueOrDefault(cacheServiceKey)) != null)
                return factoryDelegate(_resolutionStateCache.Items, _containerWeakRef, null);

            var request = (parentOrEmpty ?? _emptyRequest).Push(serviceType, serviceKey, ifUnresolved, requiredServiceType);

            var factory = ((IContainer)this).ResolveFactory(request);
            factoryDelegate = factory == null ? null : factory.GetDelegateOrDefault(request);
            if (factoryDelegate == null)
                return null;

            var resultService = factoryDelegate(request.StateCache.Items, _containerWeakRef, null);

            // Safe to cache factory only after it is evaluated without errors.
            _keyedFactoryDelegatesCache.Swap(_ => _.AddOrUpdate(serviceType,
                (factoryDelegates ?? HashTree<object, FactoryDelegate>.Empty).AddOrUpdate(cacheServiceKey, factoryDelegate)));

            return resultService;
        }

        void IResolver.ResolvePropertiesAndFields(object instance, PropertiesAndFieldsSelector selector, Request parentOrEmpty)
        {
            selector = selector ?? Rules.PropertiesAndFields ?? PropertiesAndFields.PublicNonPrimitive;

            var instanceType = instance.ThrowIfNull().GetType();
            var request = (parentOrEmpty ?? _emptyRequest).Push(instanceType).ResolveWithFactory(new InstanceFactory(instance));

            foreach (var serviceInfo in selector(request))
                if (serviceInfo != null)
                {
                    var value = request.Resolve(serviceInfo);
                    if (value != null)
                        serviceInfo.SetValue(instance, value);
                }
        }

        IEnumerable<object> IResolver.ResolveMany(Type serviceType, object serviceKey, Type requiredServiceType, object compositeParentKey)
        {
            var registeredServiceType = requiredServiceType ?? serviceType;
            var serviceFactories = ((IContainer)this).GetAllServiceFactories(registeredServiceType);

            if (serviceKey != null)             // include only single item matching key.
                serviceFactories = serviceFactories.Where(kv => serviceKey.Equals(kv.Key));

            if (compositeParentKey != null)     // exclude composite parent from items
                serviceFactories = serviceFactories.Where(kv => !compositeParentKey.Equals(kv.Key));

            foreach (var item in serviceFactories)
            {
                var service = ((IResolver)this).ResolveKeyed(serviceType, item.Key, IfUnresolved.ReturnDefault, requiredServiceType, null);
                if (service != null)            // skip unresolved items
                    yield return service;
            }
        }

        private object ResolveAndCacheDefaultDelegate(Type serviceType, IfUnresolved ifUnresolved, Request parentOrEmpty)
        {
            ThrowIfContainerDisposed();

            var request = (parentOrEmpty ?? _emptyRequest).Push(serviceType, ifUnresolved: ifUnresolved);

            var factory = ((IContainer)this).ResolveFactory(request);
            var factoryDelegate = factory == null ? null : factory.GetDelegateOrDefault(request);
            if (factoryDelegate == null)
                return null;

            var resultService = factoryDelegate(request.StateCache.Items, _containerWeakRef, null);
            _defaultFactoryDelegatesCache.Swap(_ => _.AddOrUpdate(serviceType, factoryDelegate));
            return resultService;
        }

        private void ThrowIfContainerDisposed()
        {
            this.ThrowIf(IsDisposed, Error.CONTAINER_IS_DISPOSED);
        }

        #endregion

        #region IContainer

        /// <summary>The rules object defines policies per container for registration and resolution.</summary>
        public Rules Rules { get; private set; }

        /// <summary>Indicates that container is disposed.</summary>
        public bool IsDisposed
        {
            get { return _disposed == 1; }
        }

        /// <summary>Scope associated with container.</summary>
        IScope IResolverWithScopes.SingletonScope
        {
            get { return _singletonScope; }
        }

        /// <summary>Scope associated with containers created by <see cref="Container.OpenScope"/>.</summary>
        IScope IResolverWithScopes.CurrentScope
        {
            get { return _scopeContext.GetCurrentOrDefault().ThrowIfNull(Error.NO_CURRENT_SCOPE); }
        }

        /// <summary>Empty request bound to container. All other requests are created by pushing to empty request.</summary>
        Request IContainer.EmptyRequest
        {
            get { return _emptyRequest; }
        }

        /// <summary>Self weak reference, with readable message when container is GCed/Disposed.</summary>
        ContainerWeakRef IContainer.ContainerWeakRef
        {
            get { return _containerWeakRef; }
        }

        ResolutionStateCache IContainer.ResolutionStateCache
        {
            get { return _resolutionStateCache; }
        }

        Factory IContainer.ResolveFactory(Request request)
        {
            var factory = GetServiceFactoryOrDefault(request.ServiceType, request.ServiceKey, Rules.FactorySelector);
            if (factory != null && factory.Provider != null)
                factory = factory.Provider.ProvideConcreteFactory(request);
            if (factory != null)
                return factory;

            // Try resolve factory for service type generic definition.
            var serviceTypeGenericDef = request.ServiceType.GetGenericDefinitionOrNull();
            if (serviceTypeGenericDef != null)
            {
                factory = GetServiceFactoryOrDefault(serviceTypeGenericDef, request.ServiceKey, Rules.FactorySelector);
                if (factory != null && (factory = factory.Provider.ProvideConcreteFactory(request)) != null)
                {   // Important to register produced factory, at least for recursive dependency check
                    Register(factory, request.ServiceType, request.ServiceKey, IfAlreadyRegistered.Update);
                    return factory;
                }
            }

            var resolvers = Rules.UnknownServiceResolvers;
            if (!resolvers.IsNullOrEmpty())
                for (var i = 0; i < resolvers.Length; i++)
                {
                    var ruleFactory = resolvers[i](request);
                    if (ruleFactory != null)
                        return ruleFactory;
                }

            Throw.If(request.IfUnresolved == IfUnresolved.Throw, Error.UNABLE_TO_RESOLVE_SERVICE, request);
            return null;
        }

        Factory IContainer.GetServiceFactoryOrDefault(Type serviceType, object serviceKey)
        {
            return GetServiceFactoryOrDefault(serviceType.ThrowIfNull(), serviceKey, Rules.FactorySelector, retryForOpenGeneric: true);
        }

        IEnumerable<KV<object, Factory>> IContainer.GetAllServiceFactories(Type serviceType)
        {
            var entry = _factories.Value.GetValueOrDefault(serviceType);
            if (entry == null && serviceType.IsClosedGeneric())
                entry = _factories.Value.GetValueOrDefault(serviceType.GetGenericDefinitionOrNull());

            return entry == null ? Enumerable.Empty<KV<object, Factory>>()
                : entry is Factory ? new[] { new KV<object, Factory>(DefaultKey.Value, (Factory)entry) }
                : ((FactoriesEntry)entry).Factories.Enumerate();
        }

        Expression IContainer.GetDecoratorExpressionOrDefault(Request request)
        {
            // Stop if no decorators registered.
            var decorators = _decorators.Value;
            if (decorators.IsEmpty)
                return null;

            // Decorators for non service types are not supported.
            var factoryType = request.ResolvedFactory.FactoryType;
            if (factoryType != FactoryType.Service)
                return null;

            // We are already resolving decorator for the service, so stop now.
            var parent = request.GetNonWrapperParentOrEmpty();
            if (!parent.IsEmpty && parent.ResolvedFactory.FactoryType == FactoryType.Decorator)
                return null;

            var serviceType = request.ServiceType;
            var decoratorFuncType = typeof(Func<,>).MakeGenericType(serviceType, serviceType);

            // First look for Func decorators Func<TService,TService> and initializers Action<TService>.
            var funcDecoratorExpr = GetFuncDecoratorExpressionOrDefault(decoratorFuncType, decorators, request);

            // Next look for normal decorators.
            var serviceDecorators = decorators.GetValueOrDefault(serviceType);
            var openGenericDecoratorIndex = serviceDecorators == null ? 0 : serviceDecorators.Length;
            var openGenericServiceType = request.ServiceType.GetGenericDefinitionOrNull();
            if (openGenericServiceType != null)
                serviceDecorators = serviceDecorators.Append(decorators.GetValueOrDefault(openGenericServiceType));

            Expression resultDecorator = funcDecoratorExpr;
            if (serviceDecorators != null)
            {
                for (var i = 0; i < serviceDecorators.Length; i++)
                {
                    var decorator = serviceDecorators[i];
                    var decoratorRequest = request.ResolveWithFactory(decorator);
                    if (((SetupDecorator)decorator.Setup).Condition(request))
                    {
                        // Cache closed generic registration produced by open-generic decorator.
                        if (i >= openGenericDecoratorIndex && decorator.Provider != null)
                        {
                            decorator = decorator.Provider.ProvideConcreteFactory(request);
                            Register(decorator, serviceType, null, IfAlreadyRegistered.AppendDefault);
                        }

                        var decoratorExpr = request.StateCache.GetCachedFactoryExpressionOrDefault(decorator.FactoryID);
                        if (decoratorExpr == null)
                        {
                            decoratorRequest = decoratorRequest.WithFuncArgs(decoratorFuncType);
                            decoratorExpr = decorator.GetExpressionOrDefault(decoratorRequest)
                                .ThrowIfNull(Error.CANT_CREATE_DECORATOR_EXPR, decoratorRequest);

                            var decoratedArgWasUsed = decoratorRequest.FuncArgs.Key[0];
                            decoratorExpr = !decoratedArgWasUsed ? decoratorExpr // case of replacing decorator.
                                : Expression.Lambda(decoratorFuncType, decoratorExpr, decoratorRequest.FuncArgs.Value);

                            request.StateCache.CacheFactoryExpression(decorator.FactoryID, decoratorExpr);
                        }

                        if (resultDecorator == null || !(decoratorExpr is LambdaExpression))
                            resultDecorator = decoratorExpr;
                        else
                        {
                            if (!(resultDecorator is LambdaExpression))
                                resultDecorator = Expression.Invoke(decoratorExpr, resultDecorator);
                            else
                            {
                                var prevDecorators = ((LambdaExpression)resultDecorator);
                                var decorateDecorator = Expression.Invoke(decoratorExpr, prevDecorators.Body);
                                resultDecorator = Expression.Lambda(decorateDecorator, prevDecorators.Parameters[0]);
                            }
                        }
                    }
                }
            }

            return resultDecorator;
        }

        Factory IContainer.GetWrapperFactoryOrDefault(Type serviceType)
        {
            Factory factory = null;
            if (serviceType.IsGeneric())
                factory = _wrappers.Value.GetValueOrDefault(serviceType.GetGenericDefinitionOrNull());
            if (factory == null && !serviceType.IsGenericDefinition())
                factory = _wrappers.Value.GetValueOrDefault(serviceType);
            return factory;
        }

        Type IContainer.UnwrapServiceType(Type serviceType)
        {
            var itemType = serviceType.GetElementTypeOrNull();
            if (itemType != null)
                return ((IContainer)this).UnwrapServiceType(itemType);

            var factory = ((IContainer)this).GetWrapperFactoryOrDefault(serviceType);
            if (factory == null)
                return serviceType;

            var wrapperSetup = (SetupWrapper)factory.Setup;
            var wrappedServiceType = wrapperSetup.UnwrapServiceType(serviceType);

            // Unwrap further recursively.
            return ((IContainer)this).UnwrapServiceType(wrappedServiceType);
        }

        #endregion

        #region Decorators support

        private static LambdaExpression GetFuncDecoratorExpressionOrDefault(Type decoratorFuncType,
            HashTree<Type, Factory[]> decorators, Request request)
        {
            LambdaExpression funcDecoratorExpr = null;

            var serviceType = request.ServiceType;

            // Look first for Action<ImplementedType> initializer-decorator
            var implementationType = request.ImplementationType ?? serviceType;
            var implementedTypes = implementationType.GetImplementedTypes(
                TypeTools.IncludeFlags.SourceType | TypeTools.IncludeFlags.ObjectType);

            for (var i = 0; i < implementedTypes.Length; i++)
            {
                var implementedType = implementedTypes[i];
                var initializerActionType = typeof(Action<>).MakeGenericType(implementedType);
                var initializerFactories = decorators.GetValueOrDefault(initializerActionType);
                if (initializerFactories != null)
                {
                    var doAction = _doMethod.MakeGenericMethod(implementedType, implementationType);
                    for (var j = 0; j < initializerFactories.Length; j++)
                    {
                        var initializerFactory = initializerFactories[j];
                        if (((SetupDecorator)initializerFactory.Setup).Condition(request))
                        {
                            var decoratorRequest =
                                request.UpdateServiceInfo(_ =>  ServiceInfo.Of(initializerActionType))
                                    .ResolveWithFactory(initializerFactory);
                            var actionExpr = initializerFactory.GetExpressionOrDefault(decoratorRequest);
                            if (actionExpr != null)
                                ComposeDecoratorFuncExpression(ref funcDecoratorExpr, serviceType,
                                    Expression.Call(doAction, actionExpr));
                        }
                    }
                }
            }

            // Then look for decorators registered as Func of decorated service returning decorator - Func<TService, TService>.
            var funcDecoratorFactories = decorators.GetValueOrDefault(decoratorFuncType);
            if (funcDecoratorFactories != null)
            {
                for (var i = 0; i < funcDecoratorFactories.Length; i++)
                {
                    var decoratorFactory = funcDecoratorFactories[i];
                    var decoratorRequest = request.UpdateServiceInfo(_ => ServiceInfo.Of(decoratorFuncType)).ResolveWithFactory(decoratorFactory);
                    if (((SetupDecorator)decoratorFactory.Setup).Condition(request))
                    {
                        var funcExpr = decoratorFactory.GetExpressionOrDefault(decoratorRequest);
                        if (funcExpr != null)
                            ComposeDecoratorFuncExpression(ref funcDecoratorExpr, serviceType, funcExpr);
                    }
                }
            }

            return funcDecoratorExpr;
        }

        private static void ComposeDecoratorFuncExpression(ref LambdaExpression result, Type serviceType, Expression decoratorFuncExpr)
        {
            if (result == null)
            {
                var decorated = Expression.Parameter(serviceType, "decorated");
                result = Expression.Lambda(Expression.Invoke(decoratorFuncExpr, decorated), decorated);
            }
            else
            {
                var decorateDecorator = Expression.Invoke(decoratorFuncExpr, result.Body);
                result = Expression.Lambda(decorateDecorator, result.Parameters[0]);
            }
        }

        private static readonly MethodInfo _doMethod = typeof(Container).GetSingleDeclaredMethodOrNull("DoAction");
        internal static Func<T, R> DoAction<T, R>(Action<T> action) where R : T
        {
            return x => { action(x); return (R)x; };
        }

        #endregion

        #region Factories Add/Get

        private sealed class FactoriesEntry
        {
            public readonly DefaultKey LastDefaultKey;
            public readonly HashTree<object, Factory> Factories;

            public FactoriesEntry(DefaultKey lastDefaultKey, HashTree<object, Factory> factories)
            {
                LastDefaultKey = lastDefaultKey;
                Factories = factories;
            }
        }

        private void AddOrUpdateServiceFactory(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered)
        {
            if (serviceKey == null)
                _factories.Swap(f => f.AddOrUpdate(serviceType, factory, (oldEntry, newEntry) =>
                {
                    if (oldEntry == null)
                        return newEntry;

                    var oldFactories = oldEntry as FactoriesEntry;
                    if (oldFactories != null && oldFactories.LastDefaultKey == null) // no default registered yet
                        return new FactoriesEntry(DefaultKey.Value,
                            oldFactories.Factories.AddOrUpdate(DefaultKey.Value, (Factory)newEntry));

                    switch (ifAlreadyRegistered)
                    {
                        case IfAlreadyRegistered.Throw:
                            var oldFactory = oldFactories == null ? (Factory)oldEntry
                                : oldFactories.Factories.GetValueOrDefault(oldFactories.LastDefaultKey);
                            return Throw.Instead<object>(Error.UNABLE_TO_REGISTER_DUPLICATE_DEFAULT, serviceType, oldFactory);

                        case IfAlreadyRegistered.KeepIt:
                            return oldEntry;

                        case IfAlreadyRegistered.Update:
                            return oldFactories == null ? newEntry :
                                new FactoriesEntry(oldFactories.LastDefaultKey,
                                    oldFactories.Factories.AddOrUpdate(oldFactories.LastDefaultKey, (Factory)newEntry));

                        default:
                            if (oldFactories == null)
                                return new FactoriesEntry(DefaultKey.Value.Next(),
                                    HashTree<object, Factory>.Empty
                                        .AddOrUpdate(DefaultKey.Value, (Factory)oldEntry)
                                        .AddOrUpdate(DefaultKey.Value.Next(), (Factory)newEntry));

                            var newDefaultKey = oldFactories.LastDefaultKey.Next();
                            return new FactoriesEntry(newDefaultKey,
                                oldFactories.Factories.AddOrUpdate(newDefaultKey, (Factory)newEntry));
                    }
                }));
            else // for non default service key
            {
                var factories = new FactoriesEntry(null, HashTree<object, Factory>.Empty.AddOrUpdate(serviceKey, factory));

                _factories.Swap(f => f.AddOrUpdate(serviceType, factories, (oldEntry, newEntry) =>
                {
                    if (oldEntry == null)
                        return newEntry;

                    if (oldEntry is Factory) // if registered is default, just add it to new entry
                        return new FactoriesEntry(DefaultKey.Value,
                            ((FactoriesEntry)newEntry).Factories.AddOrUpdate(DefaultKey.Value, (Factory)oldEntry));

                    var oldFactories = (FactoriesEntry)oldEntry;
                    return new FactoriesEntry(oldFactories.LastDefaultKey,
                        oldFactories.Factories.AddOrUpdate(serviceKey, factory, (oldFactory, newFactory) =>
                        {
                            if (oldFactory == null)
                                return factory;

                            switch (ifAlreadyRegistered)
                            {
                                case IfAlreadyRegistered.KeepIt:
                                    return oldFactory;

                                case IfAlreadyRegistered.Update:
                                    return newFactory;

                                //case IfAlreadyRegistered.Throw:
                                //case IfAlreadyRegistered.AppendDefault:
                                default:
                                    return Throw.Instead<Factory>(Error.UNABLE_TO_REGISTER_DUPLICATE_KEY, serviceType, serviceKey, oldFactory);
                            }
                        }));
                }));
            }
        }

        private Factory GetServiceFactoryOrDefault(Type serviceType, object serviceKey,
            Rules.FactorySelectorRule factorySelector, bool retryForOpenGeneric = false)
        {
            var entry = _factories.Value.GetValueOrDefault(serviceType);
            if (entry == null && retryForOpenGeneric && serviceType.IsClosedGeneric())
                entry = _factories.Value.GetValueOrDefault(serviceType.GetGenericDefinitionOrNull());
            if (entry == null)
                return null;

            if (factorySelector != null)
            {
                var allFactories = entry is Factory
                    ? new[] { new KeyValuePair<object, Factory>(DefaultKey.Value, (Factory)entry) }
                    : ((FactoriesEntry)entry).Factories.Enumerate()
                        .Select(f => new KeyValuePair<object, Factory>(f.Key, f.Value)).ToArray();
                var factory = factorySelector(serviceType, serviceKey, allFactories);
                return factory;
            }

            if (entry is Factory)
                return serviceKey != null && !DefaultKey.Value.Equals(serviceKey) ? null : (Factory)entry;

            var factories = ((FactoriesEntry)entry).Factories;
            if (serviceKey != null)
                return factories.GetValueOrDefault(serviceKey);

            var defaultFactories = factories.Enumerate()
                .Where(x => x.Key is DefaultKey).ToArray();

            if (defaultFactories.Length != 0)
                return defaultFactories.Length == 1
                    ? defaultFactories[0].Value
                    : Throw.Instead<Factory>(Error.EXPECTED_SINGLE_DEFAULT_FACTORY, serviceType, defaultFactories);

            return null;
        }

        #endregion

        #region Implementation

        private ContainerWeakRef _containerWeakRef;
        private readonly Request _emptyRequest;
        private int _disposed;

        private Scope _singletonScope;
        private Scope _openedScope;
        private IScopeContext _scopeContext;

        private readonly Ref<HashTree<Type, object>> _factories;        // where object is Factory or KeyedFactoriesEntry
        private readonly Ref<HashTree<Type, Factory[]>> _decorators;    // it may be multiple decorators per service type 
        private readonly Ref<HashTree<Type, Factory>> _wrappers;        // only single wrapper factory per type is supported

        private Ref<HashTree<Type, FactoryDelegate>> _defaultFactoryDelegatesCache;
        private Ref<HashTree<Type, HashTree<object, FactoryDelegate>>> _keyedFactoryDelegatesCache;
        private ResolutionStateCache _resolutionStateCache;

        private Container(Rules rules,
            Ref<HashTree<Type, object>> factories,
            Ref<HashTree<Type, Factory[]>> decorators,
            Ref<HashTree<Type, Factory>> wrappers,
            Scope singletonScope,
            IScopeContext scopeContext = null,
            Scope openedScope = null,
            int disposed = 0,
            Ref<HashTree<Type, FactoryDelegate>> resolvedDefaultDelegates = null,
            Ref<HashTree<Type, HashTree<object, FactoryDelegate>>> resolvedKeyedDelegates = null,
            ResolutionStateCache resolutionStateCache = null)
        {
            Rules = rules;

            _disposed = disposed;

            _singletonScope = singletonScope ?? new Scope();
            _openedScope = openedScope;
            _scopeContext = scopeContext ?? new ThreadScopeContext();

            _factories = factories;
            _decorators = decorators;
            _wrappers = wrappers;

            _defaultFactoryDelegatesCache = resolvedDefaultDelegates ?? Ref.Of(HashTree<Type, FactoryDelegate>.Empty);
            _keyedFactoryDelegatesCache = resolvedKeyedDelegates ?? Ref.Of(HashTree<Type, HashTree<object, FactoryDelegate>>.Empty);
            _resolutionStateCache = resolutionStateCache ?? new ResolutionStateCache();

            _containerWeakRef = new ContainerWeakRef(this);
            _emptyRequest = Request.CreateEmpty(_containerWeakRef, new WeakReference(_resolutionStateCache));
        }

        #endregion
    }

    /// <summary>Used to represent multiple default service keys. 
    /// Exposes <see cref="RegistrationOrder"/> to determine order of service added.</summary>
    public sealed class DefaultKey
    {
        /// <summary>Default value.</summary>
        public static readonly DefaultKey Value = new DefaultKey(0);

        /// <summary>Returns next default key with increased <see cref="RegistrationOrder"/>.</summary>
        /// <returns>New key.</returns>
        public DefaultKey Next()
        {
            return Of(RegistrationOrder + 1);
        }

        /// <summary>Allows to determine service registration order.</summary>
        public readonly int RegistrationOrder;

        /// <summary>Compares keys based on registration order.</summary>
        /// <param name="key">Key to compare with.</param>
        /// <returns>True if keys have the same order.</returns>
        public override bool Equals(object key)
        {
            return key == null
                || key is DefaultKey && ((DefaultKey)key).RegistrationOrder == RegistrationOrder;
        }

        /// <summary>Returns registration order as hash.</summary> <returns>Hash code.</returns>
        public override int GetHashCode()
        {
            return RegistrationOrder;
        }

        /// <summary>Prints registration order to string.</summary> <returns>Printed string.</returns>
        public override string ToString()
        {
            return "DefaultKey#" + RegistrationOrder;
        }

        #region Implementation

        private static DefaultKey[] _keyPool = { Value };

        private DefaultKey(int registrationOrder)
        {
            RegistrationOrder = registrationOrder;
        }

        private static DefaultKey Of(int registrationOrder)
        {
            if (registrationOrder < _keyPool.Length)
                return _keyPool[registrationOrder];

            var nextKey = new DefaultKey(registrationOrder);
            if (registrationOrder == _keyPool.Length)
                _keyPool = _keyPool.AppendOrUpdate(nextKey);
            return nextKey;
        }

        #endregion
    }

    /// <summary>Holds service expression cache, and state items to be passed to <see cref="FactoryDelegate"/> in resolution root.</summary>
    public sealed class ResolutionStateCache : IDisposable
    {
        /// <summary>Creates resolution state.</summary>
        public ResolutionStateCache() : this(AppendableArray.Empty, IntKeyTree.Empty, IntKeyTree.Empty) { }

        /// <summary>State item objects which may include: singleton instances for fast access, reuses, reuse wrappers, factory delegates, etc.</summary>
        public AppendableArray Items
        {
            get { return _items; }
        }

        /// <summary>Adds item if it is not already added to state, returns added or existing item index.</summary>
        /// <param name="item">Item to find in existing items with <see cref="object.Equals(object, object)"/> or add if not found.</param>
        /// <returns>Index of found or added item.</returns>
        public int GetOrAddItem(object item)
        {
            var index = -1;
            Ref.Swap(ref _items, x =>
            {
                index = x.IndexOf(item);
                if (index == -1)
                    index = (x = x.Append(item)).Length - 1;
                return x;
            });
            return index;
        }

        /// <summary>If possible wraps added item in <see cref="ConstantExpression"/> (possible for primitive type, Type, strings), 
        /// otherwise invokes <see cref="GetOrAddItem"/> and wraps access to added item (by returned index) into expression: state => state.Get(index).</summary>
        /// <param name="item">Item to wrap or to add.</param> <param name="itemType">(optional) Specific type of item, otherwise item <see cref="object.GetType()"/>.</param>
        /// <returns>Returns constant or state access expression for added items.</returns>
        public Expression GetOrAddItemExpression(object item, Type itemType = null)
        {
            itemType = itemType ?? (item == null ? typeof(object) : item.GetType());
            if (itemType.IsPrimitive() || itemType == typeof(Type))
                return Expression.Constant(item, itemType);

            var itemIndex = GetOrAddItem(item);
            var itemExpr = _itemsExpressions.GetValueOrDefault(itemIndex);
            if (itemExpr == null)
            {
                var indexExpr = Expression.Constant(itemIndex, typeof(int));
                itemExpr = Expression.Convert(Expression.Call(Container.StateParamExpr, _getItemMethod, indexExpr), itemType);
                Interlocked.Exchange(ref _itemsExpressions, _itemsExpressions.AddOrUpdate(itemIndex, itemExpr));
            }
            return (Expression)itemExpr;
        }

        /// <summary>Searches and returns cached factory expression, or null if not found.</summary>
        /// <param name="factoryID">Factory ID to lookup by.</param> <returns>Found expression or null.</returns>
        public Expression GetCachedFactoryExpressionOrDefault(int factoryID)
        {
            return _factoryExpressions.GetValueOrDefault(factoryID) as Expression;
        }

        /// <summary>Adds factory expression to cache identified by factory ID (<see cref="Factory.FactoryID"/>).</summary>
        /// <param name="factoryID">Key in cache.</param>
        /// <param name="factoryExpression">Value to cache.</param>
        public void CacheFactoryExpression(int factoryID, Expression factoryExpression)
        {
            // Not  using Ref here, because if some cache entries will be missed or replaced from another thread, 
            // it still the cache and does not affect application logic, just performance.
            Interlocked.Exchange(ref _factoryExpressions, _factoryExpressions.AddOrUpdate(factoryID, factoryExpression));
        }

        /// <summary>Removes state items and expression cache.</summary>
        public void Dispose()
        {
            _items = AppendableArray.Empty;
            _itemsExpressions = IntKeyTree.Empty;
            _factoryExpressions = IntKeyTree.Empty;
        }

        #region Implementation

        private static readonly MethodInfo _getItemMethod = typeof(AppendableArray).GetSingleDeclaredMethodOrNull("Get");

        private AppendableArray _items;
        private IntKeyTree _itemsExpressions;
        private IntKeyTree _factoryExpressions;

        private ResolutionStateCache(AppendableArray items, IntKeyTree itemsExpressions, IntKeyTree factoryExpressions)
        {
            _items = items;
            _itemsExpressions = itemsExpressions;
            _factoryExpressions = factoryExpressions;
        }

        #endregion
    }

    /// <summary>Immutable array based on wide hash tree, where each node is sub-array with predefined size: 32 is by default.
    /// Array supports only append, no remove.</summary>
    public class AppendableArray
    {
        /// <summary>Empty/default value to start from.</summary>
        public static readonly AppendableArray Empty = new AppendableArray(0);

        /// <summary>Number of items in array.</summary>
        public readonly int Length;

        /// <summary>Appends value and returns new array.</summary>
        /// <param name="value">Value to append.</param> <returns>New array.</returns>
        public virtual AppendableArray Append(object value)
        {
            return Length < NODE_ARRAY_SIZE
                ? new AppendableArray(Length + 1, _items.AppendOrUpdate(value))
                : new AppendableArrayTree(Length, IntKeyTree.Empty.AddOrUpdate(0, _items)).Append(value);
        }

        /// <summary>Returns item stored at specified index. Method relies on underlying array for index range checking.</summary>
        /// <param name="index">Index to look for item.</param> <returns>Found item.</returns>
        /// <exception cref="ArgumentOutOfRangeException">from underlying node array.</exception>
        public virtual object Get(int index)
        {
            return _items[index];
        }

        /// <summary>Returns index of first equal value in array if found, or -1 otherwise.</summary>
        /// <param name="value">Value to look for.</param> <returns>Index of first equal value, or -1 otherwise.</returns>
        public virtual int IndexOf(object value)
        {
            if (_items == null || _items.Length == 0)
                return -1;

            for (var i = 0; i < _items.Length; ++i)
            {
                var item = _items[i];
                if (ReferenceEquals(item, value) || Equals(item, value))
                    return i;
            }
            return -1;
        }

        #region Implementation

        /// <summary>Node array size. When the item added to same node, array will be copied. 
        /// So if array is too big performance will degrade. Should be power of two: e.g. 2, 4, 8, 16, 32...</summary>
        internal const int NODE_ARRAY_SIZE = 32;

        private readonly object[] _items;

        private AppendableArray(int length, object[] items = null)
        {
            Length = length;
            _items = items;
        }

        private sealed class AppendableArrayTree : AppendableArray
        {
            private const int NODE_ARRAY_BIT_MASK = NODE_ARRAY_SIZE - 1; // for length 32 will be 11111 binary.
            private const int NODE_ARRAY_BIT_COUNT = 5;                  // number of set bits in NODE_ARRAY_BIT_MASK.

            public override AppendableArray Append(object value)
            {
                var key = Length >> NODE_ARRAY_BIT_COUNT;
                var nodeItems = _tree.GetValueOrDefault(key) as object[];
                return new AppendableArrayTree(Length + 1, _tree.AddOrUpdate(key, nodeItems.AppendOrUpdate(value)));
            }

            public override object Get(int index)
            {
                return ((object[])_tree.GetValueOrDefault(index >> NODE_ARRAY_BIT_COUNT))[index & NODE_ARRAY_BIT_MASK];
            }

            public override int IndexOf(object value)
            {
                foreach (var node in _tree.Enumerate())
                {
                    var nodeItems = (object[])node.Value;
                    if (!nodeItems.IsNullOrEmpty())
                    {
                        for (var i = 0; i < nodeItems.Length; ++i)
                        {
                            var item = nodeItems[i];
                            if (ReferenceEquals(item, value) || Equals(item, value))
                                return node.Key << NODE_ARRAY_BIT_COUNT | i;
                        }
                    }
                }

                return -1;
            }

            public AppendableArrayTree(int length, IntKeyTree tree)
                : base(length)
            {
                _tree = tree;
            }

            private readonly IntKeyTree _tree;
        }

        #endregion
    }

    /// <summary>Returns reference to actual resolver implementation. 
    /// Minimizes <see cref="FactoryDelegate"/> dependency on container.</summary>
    public interface IResolverProvider
    {
        /// <summary>Provides access to resolver implementation.</summary>
        IResolverWithScopes Resolver { get; }
    }

    /// <summary>Wraps <see cref="IContainer"/> WeakReference with more specialized exceptions on access to GCed or disposed container.</summary>
    public sealed class ContainerWeakRef : IResolverProvider
    {
        public IResolverWithScopes Resolver
        {
            get { return GetTarget(); }
        }

        /// <summary>Retrieves container instance if it is not GCed or disposed</summary>
        public IContainer GetTarget(bool tryGet = false)
        {
            var container = _weakref.Target as IContainer;
            if (container == null)
            {
                Throw.If(!tryGet, Error.CONTAINER_IS_GARBAGE_COLLECTED);
                return null;
            }
            
            return container.ThrowIf(container.IsDisposed && !tryGet, Error.CONTAINER_IS_DISPOSED);
        }

        /// <summary>Creates weak reference wrapper over passed container object.</summary> <param name="container">Object to wrap.</param>
        public ContainerWeakRef(IContainer container) { _weakref = new WeakReference(container); }
        private readonly WeakReference _weakref;
    }

    /// <summary>The delegate type which is actually used to create service instance by container.
    /// Delegate instance required to be static with all information supplied by <paramref name="state"/> and <paramref name="scope"/>
    /// parameters. The requirement is due to enable compilation to DynamicMethod in DynamicAssembly, and also to simplify
    /// state management and minimizes memory leaks.</summary>
    /// <param name="state">All the state items available in resolution root (<see cref="ResolutionStateCache"/>).</param>
    /// <param name="r">Provides access to <see cref="IResolver"/> implementation to enable nested/dynamic resolve inside:
    /// registered delegate factory, <see cref="Lazy{T}"/>, and <see cref="LazyEnumerable{TService}"/>.</param>
    /// <param name="scope">Resolution root scope: initially passed value will be null, but then the actual will be created on demand.</param>
    /// <returns>Created service object.</returns>
    public delegate object FactoryDelegate(AppendableArray state, IResolverProvider r, IScope scope);

    /// <summary>Handles default conversation of expression into <see cref="FactoryDelegate"/>.</summary>
    public static partial class FactoryCompiler
    {
        /// <summary>Wraps service creation expression (body) into <see cref="FactoryDelegate"/> and returns result lambda expression.</summary>
        /// <param name="expression">Service expression (body) to wrap.</param> <returns>Created lambda expression.</returns>
        public static Expression<FactoryDelegate> WrapIntoFactoryExpression(this Expression expression)
        {
            // Removing not required Convert from expression root, because CompiledFactory result still be converted at the end.
            if (expression.NodeType == ExpressionType.Convert)
                expression = ((UnaryExpression)expression).Operand;
            if (expression.Type.IsValueType())
                expression = Expression.Convert(expression, typeof(object));
            return Expression.Lambda<FactoryDelegate>(expression,
                Container.StateParamExpr, Container.ResolverProviderParamExpr, Container.ResolutionScopeParamExpr);
        }

        /// <summary>First wraps the input service creation expression into lambda expression and
        /// then compiles lambda expression to actual <see cref="FactoryDelegate"/> used for service resolution.
        /// By default it is using Expression.Compile but if corresponding rule specified (available on .Net 4.0 and higher),
        /// it will compile to DymanicMethod/Assembly.</summary>
        /// <param name="expression">Service expression (body) to wrap.</param>
        /// <param name="rules">Specify requirement to compile expression to DynamicAssembly (available on .Net 4.0 and higher).</param>
        /// <returns>Compiled factory delegate to use for service resolution.</returns>
        public static FactoryDelegate CompileToDelegate(this Expression expression, Rules rules)
        {
            var factoryExpression = expression.WrapIntoFactoryExpression();
            FactoryDelegate factoryDelegate = null;
            CompileToMethod(factoryExpression, rules, ref factoryDelegate);
            // ReSharper disable ConstantNullCoalescingCondition
            factoryDelegate = factoryDelegate ?? factoryExpression.Compile();
            // ReSharper restore ConstantNullCoalescingCondition

            //System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(factoryDelegate.Method.MethodHandle);
            return factoryDelegate;
        }

        // Partial method definition to be implemented in .NET40 version of Container.
        // It is optional and fine to be not implemented.
        static partial void CompileToMethod(Expression<FactoryDelegate> factoryExpression, Rules rules, ref FactoryDelegate result);
    }

    /// <summary>Adds to Container support for:
    /// <list type="bullet">
    /// <item>Open-generic services</item>
    /// <item>Service generics wrappers and arrays using <see cref="Rules.UnknownServiceResolvers"/> extension point.
    /// Supported wrappers include: Func of <see cref="FuncTypes"/>, Lazy, Many, IEnumerable, arrays, Meta, KeyValuePair, DebugExpression.
    /// All wrapper factories are added into collection <see cref="Wrappers"/> and searched by <see cref="ResolveWrappers"/>
    /// unregistered resolution rule.</item>
    /// </list></summary>
    public static class WrappersSupport
    {
        /// <summary>Supported Func types up to 4 input parameters.</summary>
        public static readonly Type[] FuncTypes = { typeof(Func<>), typeof(Func<,>), typeof(Func<,,>), typeof(Func<,,,>), typeof(Func<,,,,>) };

        /// <summary>Registered wrappers by their concrete or generic definition service type.</summary>
        public static readonly HashTree<Type, Factory> Wrappers;

        static WrappersSupport()
        {
            Wrappers = HashTree<Type, Factory>.Empty;

            // Register array and its collection/list interfaces.
            var arrayExpr = new ExpressionFactory(GetArrayExpression, setup: SetupWrapper.Default);
            var arrayInterfaces = typeof(object[]).GetImplementedInterfaces()
                .Where(t => t.IsGeneric()).Select(t => t.GetGenericDefinitionOrNull());
            foreach (var arrayInterface in arrayInterfaces)
                Wrappers = Wrappers.AddOrUpdate(arrayInterface, arrayExpr);

            Wrappers = Wrappers.AddOrUpdate(typeof(LazyEnumerable<>),
                new ExpressionFactory(GetLazyEnumerableExpressionOrDefault,
                    setup: SetupWrapper.Default));

            Wrappers = Wrappers.AddOrUpdate(typeof(Lazy<>),
                new ExpressionFactory(GetLazyExpressionOrDefault, setup: SetupWrapper.Default));

            Wrappers = Wrappers.AddOrUpdate(typeof(KeyValuePair<,>),
                new ExpressionFactory(GetKeyValuePairExpressionOrDefault,
                    setup: SetupWrapper.With(t => t.GetGenericParamsAndArgs()[1])));

            Wrappers = Wrappers.AddOrUpdate(typeof(Meta<,>),
                new ExpressionFactory(GetMetaExpressionOrDefault,
                    setup: SetupWrapper.With(t => t.GetGenericParamsAndArgs()[0])));

            Wrappers = Wrappers.AddOrUpdate(typeof(ResolutionScoped<>),
                new ExpressionFactory(GetResolutionScopedExpressionOrDefault, setup: SetupWrapper.Default));

            Wrappers = Wrappers.AddOrUpdate(typeof(FactoryExpression<>),
                new ExpressionFactory(GetFactoryExpression, setup: SetupWrapper.Default));

            Wrappers = Wrappers.AddOrUpdate(typeof(Func<>),
                new ExpressionFactory(GetFuncExpression, setup: SetupWrapper.Default));

            for (var i = 0; i < FuncTypes.Length; i++)
                Wrappers = Wrappers.AddOrUpdate(FuncTypes[i],
                    new ExpressionFactory(GetFuncExpression,
                        setup: SetupWrapper.With(t => t.GetGenericParamsAndArgs().Last())));

            // Reuse wrappers
            Wrappers = Wrappers
                .AddOrUpdate(typeof(ReuseHiddenDisposable), 
                    new ExpressionFactory(GetReusedObjectWrapperExpressionOrDefault,
                        setup: SetupWrapper.With(t => typeof(object), ReuseWrapperFactory.HiddenDisposable)))

                .AddOrUpdate(typeof(ReuseWeakReference),
                    new ExpressionFactory(GetReusedObjectWrapperExpressionOrDefault,
                        setup: SetupWrapper.With(_ => typeof(object), ReuseWrapperFactory.WeakReference)))

                .AddOrUpdate(typeof(ReuseSwapable),
                    new ExpressionFactory(GetReusedObjectWrapperExpressionOrDefault,
                        setup: SetupWrapper.With(t => typeof(object), ReuseWrapperFactory.Swapable)))

                .AddOrUpdate(typeof(ReuseRecyclable),
                    new ExpressionFactory(GetReusedObjectWrapperExpressionOrDefault,
                        setup: SetupWrapper.With(t => typeof(object), ReuseWrapperFactory.Recyclable)));
        }

        /// <summary>Unregistered/fallback wrapper resolution rule.</summary>
        public static readonly Rules.UnknownServiceResolver ResolveWrappers = request =>
        {
            var serviceType = request.ServiceType;
            var itemType = serviceType.GetElementTypeOrNull();
            if (itemType != null)
                serviceType = typeof(IEnumerable<>).MakeGenericType(itemType);

            var factory = request.Container.GetWrapperFactoryOrDefault(serviceType);
            if (factory != null && factory.Provider != null)
                factory = factory.Provider.ProvideConcreteFactory(request);

            return factory;
        };

        private static Expression GetArrayExpression(Request request)
        {
            var collectionType = request.ServiceType;
            var itemType = collectionType.GetElementTypeOrNull() ?? collectionType.GetGenericParamsAndArgs()[0];

            var container = request.Container;
            var requiredItemType = container.UnwrapServiceType(request.RequiredServiceType ?? itemType);

            var items = container.GetAllServiceFactories(requiredItemType);

            // Composite pattern support: filter out composite root from available keys.
            var parent = request.GetNonWrapperParentOrEmpty();
            if (!parent.IsEmpty && parent.ServiceType == requiredItemType)
            {
                var parentFactoryID = parent.ResolvedFactory.FactoryID;
                items = items.Where(x => x.Value.FactoryID != parentFactoryID);
            }

            // Return collection of single matched item if key is specified.
            if (request.ServiceKey != null)
                items = items.Where(kv => request.ServiceKey.Equals(kv.Key));

            var itemArray = items.ToArray();
            List<Expression> itemExprList = null;
            if (itemArray.Length != 0)
            {
                itemExprList = new List<Expression>(itemArray.Length);
                for (var i = 0; i < itemArray.Length; i++)
                {
                    var item = itemArray[i];
                    var itemRequest = request.Push(itemType, item.Key, IfUnresolved.ReturnDefault);
                    var itemFactory = container.ResolveFactory(itemRequest);
                    if (itemFactory != null)
                    {
                        var itemExpr = itemFactory.GetExpressionOrDefault(itemRequest);
                        if (itemExpr != null)
                            itemExprList.Add(itemExpr);
                    }
                }
            }

            return Expression.NewArrayInit(itemType.ThrowIfNull(), itemExprList ?? Enumerable.Empty<Expression>());
        }

        private static readonly MethodInfo _resolveManyMethod =
            typeof(IResolver).GetSingleDeclaredMethodOrNull("ResolveMany").ThrowIfNull();

        private static Expression GetLazyEnumerableExpressionOrDefault(Request request)
        {
            if (IsNestedInFuncWithArgs(request))
                return null;

            var wrapperType = request.ServiceType;
            var itemServiceType = wrapperType.GetGenericParamsAndArgs()[0];
            var itemRequiredServiceType = request.Container.UnwrapServiceType(request.RequiredServiceType ?? itemServiceType);

            // Composite pattern support: find composite parent key to exclude from result.
            object compositeParentKey = null;
            var parent = request.GetNonWrapperParentOrEmpty();
            if (!parent.IsEmpty && parent.ServiceType == itemRequiredServiceType)
                compositeParentKey = parent.ServiceKey;

            var callResolveManyExpr = Expression.Call(Container.ResolverExpr, _resolveManyMethod,
                Expression.Constant(itemServiceType), 
                request.StateCache.GetOrAddItemExpression(request.ServiceKey),
                Expression.Constant(itemRequiredServiceType),
                request.StateCache.GetOrAddItemExpression(compositeParentKey));

            var getServicesExpr = Expression.Call(typeof(Enumerable), "Cast", new[] { itemServiceType }, callResolveManyExpr);

            return Expression.New(wrapperType.GetSingleConstructorOrNull().ThrowIfNull(), getServicesExpr);
        }

        private static readonly MethodInfo _resolveMethod = typeof(Resolver)
            .GetDeclaredMethodOrNull("Resolve", typeof(IResolver), typeof(object), typeof(IfUnresolved), typeof(Type)).ThrowIfNull();

        // Result: r => new Lazy<TService>(() => r.Resolver.Resolve<TService>(key, ifUnresolved, requiredType));
        private static Expression GetLazyExpressionOrDefault(Request request)
        {
            if (IsNestedInFuncWithArgs(request))
                return null;

            var wrapperType = request.ServiceType;
            var serviceType = wrapperType.GetGenericParamsAndArgs()[0];
            var wrapperCtor = wrapperType.GetConstructorOrNull(args: typeof(Func<>).MakeGenericType(serviceType));

            var serviceKeyExp = request.ServiceKey == null
                ? Expression.Constant(null, typeof(object))
                : request.StateCache.GetOrAddItemExpression(request.ServiceKey);

            var ifUnresolvedExpr = Expression.Constant(request.IfUnresolved);
            var requiredServiceTypeExpr = Expression.Constant(request.RequiredServiceType, typeof(Type));

            var resolveMethod = _resolveMethod.MakeGenericMethod(serviceType);
            var factoryExpr = Expression.Lambda(Expression.Call(resolveMethod,
                Container.ResolverExpr, serviceKeyExp, ifUnresolvedExpr, requiredServiceTypeExpr));

            return Expression.New(wrapperCtor, factoryExpr);
        }

        private static bool IsNestedInFuncWithArgs(Request request)
        {
            return !request.Parent.IsEmpty && request.Parent.Enumerate()
                .TakeWhile(r => r.ResolvedFactory.FactoryType == FactoryType.Wrapper)
                .Any(r => r.ServiceType.IsFuncWithArgs());
        }

        private static Expression GetFuncExpression(Request request)
        {
            var funcType = request.ServiceType;
            var funcArgs = funcType.GetGenericParamsAndArgs();
            var serviceType = funcArgs[funcArgs.Length - 1];

            ParameterExpression[] funcArgExprs = null;
            if (funcArgs.Length > 1)
            {
                request = request.WithFuncArgs(funcType);
                funcArgExprs = request.FuncArgs.Value;
            }

            var serviceRequest = request.Push(serviceType);
            var serviceFactory = request.Container.ResolveFactory(serviceRequest);
            var serviceExpr = serviceFactory == null ? null : serviceFactory.GetExpressionOrDefault(serviceRequest);
            return serviceExpr == null ? null : Expression.Lambda(funcType, serviceExpr, funcArgExprs);
        }

        private static Expression GetFactoryExpression(Request request)
        {
            var ctor = request.ServiceType.GetSingleConstructorOrNull().ThrowIfNull();
            var serviceType = request.ServiceType.GetGenericParamsAndArgs()[0];
            var serviceRequest = request.Push(serviceType);
            var factory = request.Container.ResolveFactory(serviceRequest);
            var expr = factory == null ? null : factory.GetExpressionOrDefault(serviceRequest);
            return expr == null ? null : Expression.New(ctor, request.StateCache.GetOrAddItemExpression(expr.WrapIntoFactoryExpression()));
        }

        private static Expression GetKeyValuePairExpressionOrDefault(Request request)
        {
            var typeArgs = request.ServiceType.GetGenericParamsAndArgs();
            var serviceKeyType = typeArgs[0];
            var serviceKey = request.ServiceKey;
            if (serviceKey == null && serviceKeyType.IsValueType() ||
                serviceKey != null && !serviceKeyType.IsTypeOf(serviceKey))
                return null;

            var serviceType = typeArgs[1];
            var serviceRequest = request.Push(serviceType, serviceKey);
            var serviceFactory = request.Container.ResolveFactory(serviceRequest);
            var serviceExpr = serviceFactory == null ? null : serviceFactory.GetExpressionOrDefault(serviceRequest);
            if (serviceExpr == null)
                return null;
            
            var pairCtor = request.ServiceType.GetSingleConstructorOrNull().ThrowIfNull();
            var keyExpr = request.StateCache.GetOrAddItemExpression(serviceKey, serviceKeyType);
            var pairExpr = Expression.New(pairCtor, keyExpr, serviceExpr);
            return pairExpr;
        }

        private static Expression GetMetaExpressionOrDefault(Request request)
        {
            var typeArgs = request.ServiceType.GetGenericParamsAndArgs();
            var metadataType = typeArgs[1];
            var serviceType = typeArgs[0];

            var container = request.Container;
            var requiredServiceType = container.UnwrapServiceType(request.RequiredServiceType ?? serviceType);

            object resultMetadata = null;
            var serviceKey = request.ServiceKey;
            if (serviceKey == null)
            {
                var result = container.GetAllServiceFactories(requiredServiceType).FirstOrDefault(kv =>
                    kv.Value.Setup.Metadata != null && metadataType.IsTypeOf(kv.Value.Setup.Metadata));
                if (result != null)
                {
                    serviceKey = result.Key;
                    resultMetadata = result.Value.Setup.Metadata;
                }
            }
            else
            {
                var factory = container.GetServiceFactoryOrDefault(requiredServiceType, serviceKey);
                if (factory != null)
                {
                    var metadata = factory.Setup.Metadata;
                    resultMetadata = metadata != null && metadataType.IsTypeOf(metadata) ? metadata : null;
                }
            }

            if (resultMetadata == null)
                return null;

            var serviceRequest = request.Push(serviceType, serviceKey);
            var serviceFactory = container.ResolveFactory(serviceRequest);
            var serviceExpr = serviceFactory == null ? null : serviceFactory.GetExpressionOrDefault(serviceRequest);
            if (serviceExpr == null)
                return null;
            var metaCtor = request.ServiceType.GetSingleConstructorOrNull().ThrowIfNull();
            var metadataExpr = request.StateCache.GetOrAddItemExpression(resultMetadata, metadataType);
            var metaExpr = Expression.New(metaCtor, serviceExpr, metadataExpr);
            return metaExpr;
        }

        private static Expression GetResolutionScopedExpressionOrDefault(Request request)
        {
            if (!request.Parent.IsEmpty)
                return null; // wrapper is only valid for resolution root.

            var wrapperType = request.ServiceType;
            var wrapperCtor = wrapperType.GetSingleConstructorOrNull();

            var serviceType = wrapperType.GetGenericParamsAndArgs()[0];
            var serviceRequest = request.Push(serviceType, request.ServiceKey);
            var serviceFactory = request.Container.ResolveFactory(serviceRequest);
            var serviceExpr = serviceFactory == null ? null : serviceFactory.GetExpressionOrDefault(serviceRequest);
            return serviceExpr == null ? null : Expression.New(wrapperCtor, serviceExpr, Container.ResolutionScopeParamExpr);
        }

        private static Expression GetReusedObjectWrapperExpressionOrDefault(Request request)
        {
            var wrapperType = request.ServiceType;
            var serviceType = request.Container.UnwrapServiceType(request.RequiredServiceType ?? wrapperType);
            var serviceRequest = request.Push(serviceType);
            var serviceFactory = request.Container.ResolveFactory(serviceRequest);
            if (serviceFactory == null)
                return null;

            var reuse = request.Container.Rules.ReuseMapping == null ? serviceFactory.Reuse
                : request.Container.Rules.ReuseMapping(serviceFactory.Reuse, serviceRequest);

            if (reuse != null && serviceFactory.Setup.ReuseWrappers.IndexOf(wrapperType.Equals) != -1)
                return serviceFactory.GetExpressionOrDefault(serviceRequest, wrapperType);
            Throw.If(request.IfUnresolved == IfUnresolved.Throw,
                Error.CANT_RESOLVE_REUSE_WRAPPER, wrapperType, serviceRequest);
            return null;
        }

        #region Tools

        /// <summary>Returns true if type is supported <see cref="FuncTypes"/>, and false otherwise.</summary>
        /// <param name="type">Type to check.</param><returns>True for func type, false otherwise.</returns>
        public static bool IsFunc(this Type type)
        {
            var genericDefinition = type.GetGenericDefinitionOrNull();
            return genericDefinition != null && FuncTypes.Contains(genericDefinition);
        }

        /// <summary>Returns true if type is func with 1 or more input arguments.</summary>
        /// <param name="type">Type to check.</param><returns>True for func type, false otherwise.</returns>
        public static bool IsFuncWithArgs(this Type type)
        {
            return type.IsFunc() && type.GetGenericDefinitionOrNull() != typeof(Func<>);
        }

        #endregion
    }

    /// <summary> Defines resolution/registration rules associated with Container instance. They may be different for different containers.</summary>
    public sealed partial class Rules
    {
        /// <summary>No rules specified.</summary>
        /// <remarks>Rules <see cref="UnknownServiceResolvers"/> are empty too.</remarks>
        public static readonly Rules Empty = new Rules();

        /// <summary>Default rules with support for generic wrappers: IEnumerable, Many, arrays, Func, Lazy, Meta, KeyValuePair, DebugExpression.
        /// Check <see cref="WrappersSupport.ResolveWrappers"/> for details.</summary>
        public static readonly Rules Default = Empty.WithUnknownServiceResolver(WrappersSupport.ResolveWrappers);

        /// <summary>Shorthand to <see cref="InjectionRules.FactoryMethod"/></summary>
        public FactoryMethodSelector FactoryMethod { get { return _injectionRules.FactoryMethod; } }

        /// <summary>Shorthand to <see cref="InjectionRules.Parameters"/></summary>
        public ParameterSelector Parameters { get { return _injectionRules.Parameters; } }

        /// <summary>Shorthand to <see cref="InjectionRules.PropertiesAndFields"/></summary>
        public PropertiesAndFieldsSelector PropertiesAndFields { get { return _injectionRules.PropertiesAndFields; } }

        /// <summary>Returns new instance of the rules with specified <see cref="InjectionRules"/>.</summary>
        /// <returns>New rules with specified <see cref="InjectionRules"/>.</returns>
        public Rules With(
            FactoryMethodSelector factoryMethod = null,
            ParameterSelector parameters = null,
            PropertiesAndFieldsSelector propertiesAndFields = null)
        {
            return new Rules(this)
            {
                _injectionRules = InjectionRules.With(
                    factoryMethod ?? _injectionRules.FactoryMethod,
                    parameters ?? _injectionRules.Parameters,
                    propertiesAndFields ?? _injectionRules.PropertiesAndFields)
            };
        }

        /// <summary>Defines single factory selector delegate.</summary>
        /// <param name="factories">Registered factories with corresponding key to select from.</param>
        /// <returns>Single selected factory, or null if unable to select.</returns>
        public delegate Factory FactorySelectorRule(
            Type serviceType, object serviceKey, KeyValuePair<object, Factory>[] factories);

        /// <summary>Rules to select single matched factory default and keyed registered factory/factories. 
        /// Selectors applied in specified array order, until first returns not null <see cref="Factory"/>.
        /// Default behavior is throw on multiple registered default factories, cause it is not obvious what to use.</summary>
        public FactorySelectorRule FactorySelector { get; private set; }

        /// <summary>Sets <see cref="FactorySelector"/></summary> 
        /// <param name="rule">Selectors to set, could be null to use default approach.</param> <returns>New rules.</returns>
        public Rules WithFactorySelector(FactorySelectorRule rule)
        {
            return new Rules(this) { FactorySelector = rule };
        }

        //we are watching you...public static
        /// <summary>Maps default to specified service key, if no factory with such a key found, then rule fall-backs to default again.
        /// Help to override default registrations in Open Scope scenarios: I may register service with key and resolve it as default in current scope.</summary>
        /// <param name="key">Service key to look for instead default.</param>
        /// <returns>Found factory or null.</returns>
        public static FactorySelectorRule MapDefaultToKey(object key)
        {
            return (t, k, fs) => k == null
                ? fs.FirstOrDefault(f => f.Key.Equals(key)).Value
                ?? fs.FirstOrDefault(f => f.Key.Equals(null)).Value
                : fs.FirstOrDefault(f => f.Key.Equals(k)).Value;
        }

        /// <summary>Defines delegate to return factory for request not resolved by registered factories or prior rules.
        /// Applied in specified array order until return not null <see cref="Factory"/>.</summary> 
        /// <param name="request">Request to return factory for</param> <returns>Factory to resolve request, or null if unable to resolve.</returns>
        public delegate Factory UnknownServiceResolver(Request request);

        /// <summary>Gets rules for resolving not-registered services. Null by default.</summary>
        public UnknownServiceResolver[] UnknownServiceResolvers { get; private set; }

        /// <summary>Appends resolver to current unknown service resolvers.</summary>
        /// <param name="rule">Rule to append.</param> <returns>New Rules.</returns>
        public Rules WithUnknownServiceResolver(UnknownServiceResolver rule)
        {
            return new Rules(this) { UnknownServiceResolvers = UnknownServiceResolvers.AppendOrUpdate(rule) };
        }

        /// <summary>Removes specified resolver from unknown service resolvers, and returns new Rules.
        /// If no resolver was found then <see cref="UnknownServiceResolvers"/> will stay the same instance, 
        /// so it could be check for remove success or fail.</summary>
        /// <param name="rule">Rule tor remove.</param> <returns>New rules.</returns>
        public Rules WithoutUnknownServiceResolver(UnknownServiceResolver rule)
        {
            return new Rules(this) { UnknownServiceResolvers = UnknownServiceResolvers.Remove(rule) };
        }

        /// <summary>Turns on/off exception throwing when dependency has shorter reuse lifespan than its parent.</summary>
        public bool ThrowIfDepenedencyHasShorterReuseLifespan { get; private set; }

        /// <summary>Returns new rules with <see cref="ThrowIfDepenedencyHasShorterReuseLifespan"/> set to specified value.</summary>
        /// <returns>New rules with new setting value.</returns>
        public Rules WithoutThrowIfDepenedencyHasShorterReuseLifespan()
        {
            return new Rules(this) { ThrowIfDepenedencyHasShorterReuseLifespan = false };
        }

        /// <summary>Defines mapping from registered reuse to what will be actually used.</summary>
        /// <param name="reuse">Service registered reuse</param> <param name="request">Context.</param> <returns>Mapped result reuse to use.</returns>
        public delegate IReuse ReuseMappingRule(IReuse reuse, Request request);

        /// <summary>Gets rule to retrieve actual reuse from registered one. May be null, so the registered reuse will be used.
        /// Could be used to specify different reuse container wide, for instance <see cref="Reuse.Singleton"/> instead of <see cref="Reuse.Transient"/>.</summary>
        public ReuseMappingRule ReuseMapping { get; private set; }

        /// <summary>Sets the <see cref="ReuseMapping"/> rule.</summary> <param name="rule">Rule to set, may be null.</param> <returns>New rules.</returns>
        public Rules WithReuseMapping(ReuseMappingRule rule)
        {
            return new Rules(this) { ReuseMapping = rule };
        }

        /// <summary>Allow to instantiate singletons during resolution (but not inside of Func). Instantiated singletons
        /// will be copied to <see cref="ResolutionStateCache"/> for faster access.</summary>
        public bool SingletonOptimization { get; private set; }

        /// <summary>Disables <see cref="SingletonOptimization"/></summary>
        /// <returns>New rules with singleton optimization turned off.</returns>
        public Rules WithoutSingletonOptimization()
        {
            return new Rules(this) { SingletonOptimization = false };
        }

        public delegate void BeforeFactoryRegistrationHandler(Factory factory, Type serviceType, object optServiceKey);

        public BeforeFactoryRegistrationHandler BeforeFactoryRegistrationHook { get; private set; }

        public Rules WithBeforeFactoryRegistrationHook(BeforeFactoryRegistrationHandler hook)
        {
            return new Rules(this) { BeforeFactoryRegistrationHook = hook };
        }

        #region Implementation

        private InjectionRules _injectionRules;
        private bool _compilationToDynamicAssemblyEnabled; // NOTE: used by .NET 4 and higher versions.

        private Rules()
        {
            _injectionRules = InjectionRules.Default;
            ThrowIfDepenedencyHasShorterReuseLifespan = true;
            SingletonOptimization = true;
        }

        private Rules(Rules copy)
        {
            FactorySelector = copy.FactorySelector;
            UnknownServiceResolvers = copy.UnknownServiceResolvers;
            ThrowIfDepenedencyHasShorterReuseLifespan = copy.ThrowIfDepenedencyHasShorterReuseLifespan;
            ReuseMapping = copy.ReuseMapping;
            SingletonOptimization = copy.SingletonOptimization;
            BeforeFactoryRegistrationHook = copy.BeforeFactoryRegistrationHook;
            _injectionRules = copy._injectionRules;
            _compilationToDynamicAssemblyEnabled = copy._compilationToDynamicAssemblyEnabled;
        }

        #endregion
    }

    /// <summary>Wraps constructor or factory method optionally with factory instance to create service.</summary>
    public sealed class FactoryMethod
    {
        /// <summary><see cref="ConstructorInfo"/> or <see cref="MethodInfo"/> for factory method.</summary>
        public readonly MethodBase Method;

        /// <summary>Factory instance if <see cref="Method"/> is instance factory method.</summary>
        public readonly object Factory;

        /// <summary>For convenience conversation from method to its wrapper.</summary>
        /// <param name="method">Method to wrap.</param> <returns>Factory method wrapper.</returns>
        public static implicit operator FactoryMethod(MethodBase method)
        {
            return Of(method);
        }

        /// <summary>Converts method to <see cref="FactoryMethodSelector"/> ignoring request.</summary>
        /// <param name="method">Method to convert.</param> <returns>New selector</returns>
        public static implicit operator FactoryMethodSelector(FactoryMethod method)
        {
            return request => method;
        }

        /// <summary>Wraps method and factory instance.</summary>
        /// <param name="method">Static or instance method.</param> <param name="factory">Factory instance in case of instance <paramref name="method"/>.</param>
        /// <returns>New factory method wrapper.</returns>
        public static FactoryMethod Of(MethodBase method, object factory = null)
        {
            return new FactoryMethod(method.ThrowIfNull(), factory);
        }

        /// <summary>Creates factory method using refactoring friendly static method call expression (without string method name).
        /// You can supply any/default arguments to factory method, they won't be used, it is only to find the <see cref="MethodInfo"/>.</summary>
        /// <typeparam name="TService">Factory product type.</typeparam> <param name="method">Static method call expression.</param>
        /// <returns>New factory method wrapper.</returns>
        public static FactoryMethod Of<TService>(Expression<Func<TService>> method)
        {
            var methodInfo = ExpressionTools.GetCalledMethodOrNull(method);
            return new FactoryMethod(methodInfo.ThrowIfNull().ThrowIf(!methodInfo.IsStatic));
        }

        /// <summary>Creates factory method using refactoring friendly instance method call expression (without string method name).
        /// You can supply any/default arguments to factory method, they won't be used, it is only to find the <see cref="MethodInfo"/>.</summary>
        /// <typeparam name="TFactory">Factory type.</typeparam> <typeparam name="TService">Factory product type.</typeparam>
        /// <param name="getFactory">Returns or resolves factory instance.</param> <param name="method">Method call expression.</param>
        /// <returns>New factory method wrapper.</returns>
        public static FactoryMethodSelector
            Of<TFactory, TService>(Func<Request, TFactory> getFactory, Expression<Func<TFactory, TService>> method)
            where TFactory : class
        {
            var methodInfo = ExpressionTools.GetCalledMethodOrNull(method);
            return r => new FactoryMethod(methodInfo.ThrowIfNull(), getFactory(r).ThrowIfNull());
        }

        /// <summary>Pretty prints wrapped method.</summary> <returns>Printed string.</returns>
        public override string ToString()
        {
            return new StringBuilder().Print(Method.DeclaringType).Append("::[").Append(Method).Append("]").ToString();
        }

        private FactoryMethod(MethodBase method, object factory = null)
        {
            Method = method;
            Factory = factory;
        }
    }

    /// <summary>Rules to dictate Container or registered implementation (<see cref="ReflectionFactory"/>) how to:
    /// <list type="bullet">
    /// <item>Select constructor for creating service with <see cref="FactoryMethod"/>.</item>
    /// <item>Specify how to resolve constructor parameters with <see cref="Parameters"/>.</item>
    /// <item>Specify what properties/fields to resolve and how with <see cref="PropertiesAndFields"/>.</item>
    /// </list></summary>
    public class InjectionRules
    {
        /// <summary>No rules specified.</summary>
        public static readonly InjectionRules Default = new InjectionRules();

        /// <summary>Specifies injections rules for Constructor, Parameters, Properties and Fields. If no rules specified returns <see cref="Default"/> rules.</summary>
        /// <param name="factoryMethod">(optional)</param> <param name="parameters">(optional)</param> <param name="propertiesAndFields">(optional)</param>
        /// <returns>New injection rules or <see cref="Default"/>.</returns>
        public static InjectionRules With(
            FactoryMethodSelector factoryMethod = null,
            ParameterSelector parameters = null,
            PropertiesAndFieldsSelector propertiesAndFields = null)
        {
            return factoryMethod == null && parameters == null && propertiesAndFields == null
                ? Default : new InjectionRules(factoryMethod, parameters, propertiesAndFields);
        }

        /// <summary>Sets rule how to select constructor with simplified signature without <see cref="Request"/> 
        /// and <see cref="IContainer"/> parameters.</summary>
        /// <param name="getConstructor">Rule delegate taking implementation type as input and returning selected constructor info.</param>
        /// <returns>New instance of <see cref="InjectionRules"/> with <see cref="FactoryMethod"/> set to specified delegate.</returns>
        public InjectionRules With(Func<Type, ConstructorInfo> getConstructor)
        {
            return getConstructor == null ? this
                : new InjectionRules(r => getConstructor(r.ImplementationType), Parameters, PropertiesAndFields);
        }

        /// <summary>Creates rules with only <see cref="FactoryMethod"/> specified.</summary>
        /// <param name="factoryMethod">To use.</param> <returns>New rules.</returns>
        public static implicit operator InjectionRules(FactoryMethodSelector factoryMethod)
        {
            return With(factoryMethod);
        }


        /// <summary>Creates rules with only <see cref="FactoryMethod"/> specified.</summary>
        /// <param name="factoryMethod">To return from <see cref="FactoryMethod"/>.</param> <returns>New rules.</returns>
        public static implicit operator InjectionRules(FactoryMethod factoryMethod)
        {
            return With(_ => factoryMethod);
        }

        /// <summary>Creates rules with only <see cref="FactoryMethod"/> specified.</summary>
        /// <param name="factoryMethod">To create <see cref="DryIoc.FactoryMethod"/> and return it from <see cref="FactoryMethod"/>.</param> 
        /// <returns>New rules.</returns>
        public static implicit operator InjectionRules(MethodInfo factoryMethod)
        {
            return With(_ => DryIoc.FactoryMethod.Of(factoryMethod));
        }

        /// <summary>Creates rules with only <see cref="Parameters"/> specified.</summary>
        /// <param name="parameters">To use.</param> <returns>New rules.</returns>
        public static implicit operator InjectionRules(ParameterSelector parameters)
        {
            return With(parameters: parameters);
        }

        /// <summary>Creates rules with only <see cref="PropertiesAndFields"/> specified.</summary>
        /// <param name="propertiesAndFields">To use.</param> <returns>New rules.</returns>
        public static implicit operator InjectionRules(PropertiesAndFieldsSelector propertiesAndFields)
        {
            return With(propertiesAndFields: propertiesAndFields);
        }

        /// <summary>Returns delegate to select constructor based on provided request.</summary>
        public FactoryMethodSelector FactoryMethod { get; private set; }

        /// <summary>Specifies how constructor parameters should be resolved: 
        /// parameter service key and type, throw or return default value if parameter is unresolved.</summary>
        public ParameterSelector Parameters { get; private set; }

        /// <summary>Specifies what <see cref="ServiceInfo"/> should be used when resolving property or field.</summary>
        public PropertiesAndFieldsSelector PropertiesAndFields { get; private set; }

        #region Implementation

        private InjectionRules() { }

        private InjectionRules(
            FactoryMethodSelector factoryMethod = null,
            ParameterSelector parameters = null,
            PropertiesAndFieldsSelector propertiesAndFields = null)
        {
            FactoryMethod = factoryMethod;
            Parameters = parameters;
            PropertiesAndFields = propertiesAndFields;
        }

        #endregion
    }

    /// <summary>Contains <see cref="IRegistrator"/> extension methods to simplify general use cases.</summary>
    public static class Registrator
    {
        /// <summary>Registers service of <paramref name="serviceType"/>.</summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The service type to register</param>
        /// <param name="factory"><see cref="Factory"/> details object.</param>
        /// <param name="named">(optional) service key (name). Could be of any type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator, Type serviceType, Factory factory,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
        {
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>Registers service of <typeparamref name="TService"/>.</summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="factory"><see cref="Factory"/> details object.</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TService>(this IRegistrator registrator, Factory factory,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
        {
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>Registers service <paramref name="serviceType"/> with corresponding <paramref name="implementationType"/>.</summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The service type to register.</param>
        /// <param name="implementationType">Implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">
        /// (optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. 
        /// Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="rules">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) Factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) Service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) Policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator, Type serviceType, Type implementationType,
            IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null, InjectionRules rules = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
        {
            rules = (rules ?? InjectionRules.Default).With(withConstructor);
            var factory = new ReflectionFactory(implementationType, reuse, rules, setup);
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>Registers service of <paramref name="implementationAndServiceType"/>. ServiceType will be the same as <paramref name="implementationAndServiceType"/>.</summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="implementationAndServiceType">Implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="rules">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator,
            Type implementationAndServiceType, IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null,
            InjectionRules rules = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
        {
            rules = (rules ?? InjectionRules.Default).With(withConstructor);
            var factory = new ReflectionFactory(implementationAndServiceType, reuse, rules, setup);
            registrator.Register(factory, implementationAndServiceType, named, ifAlreadyRegistered);
        }

        /// <summary>Registers service of <typeparamref name="TService"/> type implemented by <typeparamref name="TImplementation"/> type.</summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="with">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TService, TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null,
            InjectionRules with = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
            where TImplementation : TService
        {
            with = (with ?? InjectionRules.Default).With(withConstructor);
            var factory = new ReflectionFactory(typeof(TImplementation), reuse, with, setup);
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>Registers implementation type <typeparamref name="TServiceAndImplementation"/> with itself as service type.</summary>
        /// <typeparam name="TServiceAndImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="with">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) Factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) Service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) Policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TServiceAndImplementation>(this IRegistrator registrator,
            IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null,
            InjectionRules with = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
        {
            with = (with ?? InjectionRules.Default).With(withConstructor);
            var factory = new ReflectionFactory(typeof(TServiceAndImplementation), reuse, with, setup);
            registrator.Register(factory, typeof(TServiceAndImplementation), named, ifAlreadyRegistered);
        }

        /// <summary>Returns true if type is public and not an object type. 
        /// Provides default setting for <see cref="RegisterAll"/> "types" parameter. </summary>
        /// <param name="type">Type to check.</param> <returns>True for matched type, false otherwise.</returns>
        public static bool DefaultServiceTypesForRegisterAll(Type type)
        {
            return type.IsPublicOrNestedPublic() && type != typeof(object);
        }

        /// <summary>Registers single registration for all implemented public interfaces and base classes.</summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="implementationType">Service implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="rules">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="whereServiceTypes">(optional) condition to include selected types only. Default value is <see cref="DefaultServiceTypesForRegisterAll"/></param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterAll(this IRegistrator registrator, Type implementationType,
            IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null,
            InjectionRules rules = null, FactorySetup setup = null, Func<Type, bool> whereServiceTypes = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
        {
            rules = (rules ?? InjectionRules.Default).With(withConstructor);
            var factory = new ReflectionFactory(implementationType, reuse, rules, setup);

            var implementedTypes = implementationType.GetImplementedTypes(TypeTools.IncludeFlags.SourceType);
            var serviceTypes = implementedTypes.Where(whereServiceTypes ?? DefaultServiceTypesForRegisterAll);
            if (implementationType.IsGenericDefinition())
            {
                var implTypeArgs = implementationType.GetGenericParamsAndArgs();
                serviceTypes = serviceTypes
                    .Where(t => t.ContainsAllGenericParameters(implTypeArgs))
                    .Select(t => t.GetGenericDefinitionOrNull());
            }

            var atLeastOneRegistered = false;
            foreach (var serviceType in serviceTypes)
            {
                registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
                atLeastOneRegistered = true;
            }

            Throw.If(!atLeastOneRegistered, Error.NO_SERVICE_TYPE_TO_REGISTER_ALL, implementationType, implementedTypes);
        }

        /// <summary>Registers single registration for all implemented public interfaces and base classes.</summary>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">(optional) strategy to select constructor when multiple available.</param>
        /// <param name="with">(optional) specifies <see cref="InjectionRules"/>.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="types">(optional) condition to include selected types only. Default value is <see cref="DefaultServiceTypesForRegisterAll"/></param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterAll<TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, Func<Type, ConstructorInfo> withConstructor = null,
            InjectionRules with = null, FactorySetup setup = null, Func<Type, bool> types = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
        {
            registrator.RegisterAll(typeof(TImplementation),
                reuse, withConstructor, with, setup, types, named, ifAlreadyRegistered);
        }

        /// <summary>Registers a factory delegate for creating an instance of <typeparamref name="TService"/>.
        /// Delegate can use <see cref="IResolver"/> parameter to resolve any required dependencies, e.g.:
        /// <code>RegisterDelegate&lt;ICar&gt;(r => new Car(r.Resolve&lt;IEngine&gt;()))</code></summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="factoryDelegate">The delegate used to create a instance of <typeparamref name="TService"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterDelegate<TService>(this IRegistrator registrator,
            Func<IResolver, TService> factoryDelegate, IReuse reuse = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
        {
            var factory = new DelegateFactory(r => factoryDelegate(r), reuse, setup);
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>Registers a factory delegate for creating an instance of <paramref name="serviceType"/>.
        /// Delegate can use <see cref="IResolver"/> parameter to resolve any required dependencies, e.g.:
        /// <code>RegisterDelegate&lt;ICar&gt;(r => new Car(r.Resolve&lt;IEngine&gt;()))</code></summary>
        /// <param name="serviceType">Service type to register.</param>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="factoryDelegate">The delegate used to create a instance of <paramref name="serviceType"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterDelegate(this IRegistrator registrator, Type serviceType,
            Func<IResolver, object> factoryDelegate, IReuse reuse = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
        {
            Func<IResolver, object> checkedDelegate = r => factoryDelegate(r)
                .ThrowIfNotOf(serviceType, Error.REGED_FACTORY_DLG_RESULT_NOT_OF_SERVICE_TYPE, r);
            var factory = new DelegateFactory(checkedDelegate, reuse, setup);
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>Registers a pre-created object of <typeparamref name="TService"/>.
        /// It is just a sugar on top of <see cref="RegisterDelegate{TService}"/> method.</summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="instance">The pre-created instance of <typeparamref name="TService"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterInstance<TService>(this IRegistrator registrator, TService instance, IReuse reuse = null,
            FactorySetup setup = null, object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
        {
            var factory = reuse == null
                ? (Factory)new InstanceFactory(instance, setup)
                : new DelegateFactory(_ => instance, reuse, setup);
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>Registers a pre-created object assignable to <paramref name="serviceType"/>. </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">Service type to register.</param>
        /// <param name="instance">The pre-created instance of <paramref name="serviceType"/>.</param>
        /// <param name="reuse">(optional) <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">(optional) factory setup, by default is (<see cref="Setup"/>)</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">(optional) policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterInstance(this IRegistrator registrator, Type serviceType, object instance, IReuse reuse = null,
            FactorySetup setup = null, object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.AppendDefault)
        {
            var factory = reuse == null
                ? (Factory)new InstanceFactory(instance, setup)
                : new DelegateFactory(_ => instance, reuse, setup);
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>Registers initializing action that will be called after service is resolved just before returning it to caller.
        /// Check example below for using initializer to automatically subscribe to singleton event aggregator.
        /// You can register multiple initializers for single service. 
        /// Or you can register initializer for <see cref="Object"/> type to be applied for all services and use <see cref="condition"/> 
        /// to filter target services.</summary>
        /// <remarks>Initializer internally implemented as decorator registered as Action delegate, so all decorators behavior is applied.</remarks>
        /// <typeparam name="TTarget">Any type implemented by requested service type including service type itself and object type.</typeparam>
        /// <param name="registrator">Usually is <see cref="Container"/> object.</param>
        /// <param name="initialize">Delegate with <typeparamref name="TTarget"/> object and 
        /// <see cref="IResolver"/> to resolve additional services required by initializer.</param>
        /// <param name="condition">(optional) Condition to select required target.</param>
        /// <example><code lang="cs"><![CDATA[
        ///     container.Register<EventAggregator>(Reuse.Singleton);
        ///     container.Register<ISubscriber, SomeSubscriber>();
        /// 
        ///     // Registers initializer for all subscribers implementing ISubscriber.
        ///     container.RegisterInitiliazer<ISubscriber>((s, r) => r.Resolve<EventAggregator>().Subscribe(s));
        /// ]]></code></example>
        public static void RegisterInitializer<TTarget>(this IRegistrator registrator,
            Action<TTarget, IResolver> initialize, Func<Request, bool> condition = null)
        {
            registrator.RegisterDelegate<Action<TTarget>>(r => target => initialize(target, r), setup: SetupDecorator.With(condition));
        }

        /// <summary>Returns true if <paramref name="serviceType"/> is registered in container or its open generic definition is registered in container.</summary>
        /// <param name="registrator">Usually <see cref="Container"/> to explore or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="serviceType">The type of the registered service.</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">(optional) factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">(optional) condition to specify what registered factory do you expect.</param>
        /// <returns>True if <paramref name="serviceType"/> is registered, false - otherwise.</returns>
        public static bool IsRegistered(this IRegistrator registrator, Type serviceType,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            return registrator.IsRegistered(serviceType, named, factoryType, condition);
        }

        /// <summary>Returns true if <typeparamref name="TService"/> type is registered in container or its open generic definition is registered in container.</summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Usually <see cref="Container"/> to explore or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">(optional) factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">(optional) condition to specify what registered factory do you expect.</param>
        /// <returns>True if <typeparamref name="TService"/> name="serviceType"/> is registered, false - otherwise.</returns>
        public static bool IsRegistered<TService>(this IRegistrator registrator,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            return registrator.IsRegistered(typeof(TService), named, factoryType, condition);
        }

        /// <summary>Removes specified registration from container.</summary>
        /// <param name="registrator">Usually <see cref="Container"/> to explore or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="serviceType">Type of service to remove.</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">(optional) factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">(optional) condition for Factory to be removed.</param>
        public static void Unregister(this IRegistrator registrator, Type serviceType,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            registrator.Unregister(serviceType, named, factoryType, condition);
        }

        /// <summary>Removes specified registration from container.</summary>
        /// <typeparam name="TService">The type of service to remove.</typeparam>
        /// <param name="registrator">Usually <see cref="Container"/> or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="named">(optional) service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">(optional) factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">(optional) condition for Factory to be removed.</param>
        public static void Unregister<TService>(this IRegistrator registrator,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            registrator.Unregister(typeof(TService), named, factoryType, condition);
        }

        /// <summary>Scans provided assemblies for implementation types of specified <paramref name="serviceType"/>
        /// and registers all of them in container with specified <paramref name="reuse"/> policy.</summary>
        /// <param name="registrator">Usually <see cref="Container"/> or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="serviceType">Service type to look implementations for.</param>
        /// <param name="typeProvider">Provides types to peek implementation type from and register.</param>
        /// <param name="reuse">(optional)Reuse policy, Transient if not specified.</param>
        public static void RegisterBatch(this IRegistrator registrator, Type serviceType,  IEnumerable<Type> typeProvider, IReuse reuse = null)
        {
            var implTypes = typeProvider.ThrowIfNull().Where(type => IsImplementationOf(type, serviceType)).ToArray();
            for (var i = 0; i < implTypes.Length; ++i)
                registrator.Register(serviceType, implTypes[i], reuse);
        }

        /// <summary>Scans provided assemblies for implementation types of specified <typeparamref name="TService"/>
        /// and registers all of them in container with specified <paramref name="reuse"/> policy.</summary>
        /// <typeparam name="TService">Service type to look implementations for.</typeparam>
        /// <param name="registrator">Usually <see cref="Container"/> or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="typeProvider">Provides types to peek implementation type from and register.</param>
        /// <param name="reuse">(optional)Reuse policy, Transient if not specified.</param>
        public static void RegisterBatch<TService>(this IRegistrator registrator, IEnumerable<Type> typeProvider, IReuse reuse = null)
        {
            registrator.RegisterBatch(typeof(TService), typeProvider, reuse);
        }

        /// <summary>Scans provided assemblies for implementation types of specified service type.
        /// and registers all of them in container with specified <paramref name="reuse"/> policy.</summary>
        /// <param name="serviceType">Service type to look implementations for.</param>
        /// <param name="registrator">Usually <see cref="Container"/> or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="assemblyProvider">Provides assembly to scan for implementation types and register them for service.</param>
        /// <param name="reuse">(optional)Reuse policy, Transient if not specified.</param>
        public static void RegisterBatch(this IRegistrator registrator, Type serviceType, IEnumerable<Assembly> assemblyProvider, IReuse reuse = null)
        {
            registrator.RegisterBatch(serviceType, assemblyProvider.ThrowIfNull().SelectMany(Portable.GetTypesFromAssembly));
        }

        private static bool IsImplementationOf(Type candidateImplType, Type serviceType)
        {
            if (candidateImplType.IsAbstract() || !serviceType.IsPublicOrNestedPublic())
                return false;

            if (candidateImplType == serviceType)
                return true;

            var implementedTypes = candidateImplType.GetImplementedTypes();

            var found = !serviceType.IsOpenGeneric()
                ? implementedTypes.Contains(serviceType)
                : implementedTypes.Any(t => t.GetGenericDefinitionOrNull() == serviceType);

            return found;
        }
    }

    /// <summary>Defines convenient extension methods for <see cref="IResolver"/>.</summary>
    public static class Resolver
    {
        /// <summary>Returns instance of <typepsaramref name="TService"/> type.</summary>
        /// <param name="serviceType">The type of the requested service.</param>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="ifUnresolved">(optional) Says how to handle unresolved service.</param>
        /// <returns>The requested service instance.</returns>
        public static object Resolve(this IResolver resolver, Type serviceType, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return resolver.ResolveDefault(serviceType, ifUnresolved, null);
        }

        /// <summary>Returns instance of <typepsaramref name="TService"/> type.</summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="ifUnresolved">(optional) Says how to handle unresolved service.</param>
        /// <returns>The requested service instance.</returns>
        public static TService Resolve<TService>(this IResolver resolver, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return (TService)resolver.ResolveDefault(typeof(TService), ifUnresolved, null);
        }

        /// <summary>Returns instance of <typeparamref name="TService"/> searching for <paramref name="requiredServiceType"/>.
        /// In case of <typeparamref name="TService"/> being generic wrapper like Func, Lazy, IEnumerable, etc., <paramref name="requiredServiceType"/>
        /// could specify wrapped service type.</summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="requiredServiceType">(optional) Service or wrapped type assignable to <typeparamref name="TService"/>.</param>
        /// <param name="ifUnresolved">(optional) Says how to handle unresolved service.</param>
        /// <returns>The requested service instance.</returns>
        /// <remarks>Using <paramref name="requiredServiceType"/> implicitly support Covariance for generic wrappers even in .Net 3.5.</remarks>
        /// <example><code lang="cs"><![CDATA[
        ///     container.Register<IService, Service>();
        ///     var services = container.Resolve<IEnumerable<object>>(typeof(IService));
        /// ]]></code></example>
        public static TService Resolve<TService>(this IResolver resolver, Type requiredServiceType, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return (TService)resolver.ResolveKeyed(typeof(TService), null, ifUnresolved, requiredServiceType, null);
        }

        /// <summary>Returns instance of <paramref name="serviceType"/> searching for <paramref name="requiredServiceType"/>.
        /// In case of <paramref name="serviceType"/> being generic wrapper like Func, Lazy, IEnumerable, etc., <paramref name="requiredServiceType"/>
        /// could specify wrapped service type.</summary>
        /// <param name="serviceType">The type of the requested service.</param>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceKey">Service key (any type with <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/> defined).</param>
        /// <param name="ifUnresolved">(optional) Says how to handle unresolved service.</param>
        /// <param name="requiredServiceType">(optional) Service or wrapped type assignable to <paramref name="serviceType"/>.</param>
        /// <returns>The requested service instance.</returns>
        /// <remarks>Using <paramref name="requiredServiceType"/> implicitly support Covariance for generic wrappers even in .Net 3.5.</remarks>
        /// <example><code lang="cs"><![CDATA[
        ///     container.Register<IService, Service>();
        ///     var services = container.Resolve(typeof(Lazy<object>), "named", requiredServiceType: typeof(IService));
        /// ]]></code></example>
        public static object Resolve(this IResolver resolver, Type serviceType, object serviceKey,
            IfUnresolved ifUnresolved = IfUnresolved.Throw, Type requiredServiceType = null)
        {
            return serviceKey == null && requiredServiceType == null
                ? resolver.ResolveDefault(serviceType, ifUnresolved, null)
                : resolver.ResolveKeyed(serviceType, serviceKey, ifUnresolved, requiredServiceType, null);
        }

        /// <summary>Resolve service using provided specification with <paramref name="info"/>. Useful for DryIoc extensions and hooks.</summary>
        /// <param name="resolver">Container to resolve from</param> <param name="info">Service specification.</param>
        /// <returns>Resolved service object.</returns>
        public static object Resolve(this IResolver resolver, IServiceInfo info)
        {
            var details = info.Details;
            return details.GetValue != null ? details.GetValue(resolver)
                : resolver.Resolve(info.ServiceType, details.ServiceKey, details.IfUnresolved, details.RequiredServiceType);
        }

        /// <summary>Returns instance of <typepsaramref name="TService"/> type.</summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceKey">Service key (any type with <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/> defined).</param>
        /// <param name="ifUnresolved">(optional) Says how to handle unresolved service.</param>
        /// <param name="requiredServiceType">(optional) Service or wrapped type assignable to <typeparamref name="TService"/>.</param>
        /// <returns>The requested service instance.</returns>
        /// <remarks>Using <paramref name="requiredServiceType"/> implicitly support Covariance for generic wrappers even in .Net 3.5.</remarks>
        public static TService Resolve<TService>(this IResolver resolver, object serviceKey,
            IfUnresolved ifUnresolved = IfUnresolved.Throw, Type requiredServiceType = null)
        {
            return (TService)resolver.Resolve(typeof(TService), serviceKey, ifUnresolved, requiredServiceType);
        }

        /// <summary>Returns all registered services instances including all keyed and default registrations.
        /// Use <paramref name="behavior"/> to return either all registered services at the moment of resolve (dynamic fresh view) or
        /// the same services that were returned with first <see cref="ResolveMany{TService}"/> call (fixed view).</summary>
        /// <typeparam name="TService">Return collection item type. It denotes registered service type if <paramref name="requiredServiceType"/> is not specified.</typeparam>
        /// <param name="resolver">Usually <see cref="Container"/> object.</param>
        /// <param name="requiredServiceType">(optional) Denotes registered service type. Should be assignable to <typeparamref name="TService"/>.</param>
        /// <param name="behavior">(optional) Specifies new registered services awareness. Aware by default.</param>
        /// <returns>Result collection of services.</returns>
        /// <remarks>The same result could be achieved by directly calling:
        /// <code lang="cs"><![CDATA[
        ///     container.Resolve<LazyEnumerable<IService>>();  // for dynamic result - default behavior
        ///     container.Resolve<IService[]>();                // for fixed array
        ///     container.Resolve<IEnumerable<IService>>();     // same as fixed array
        /// ]]></code>
        /// </remarks>
        public static IEnumerable<TService> ResolveMany<TService>(this IResolver resolver,
            Type requiredServiceType = null, ResolveManyBehavior behavior = ResolveManyBehavior.EachItemLazyResolved)
        {
            return behavior == ResolveManyBehavior.EachItemLazyResolved
                ? resolver.Resolve<LazyEnumerable<TService>>(requiredServiceType)
                : resolver.Resolve<IEnumerable<TService>>(requiredServiceType);
        }

        /// <summary>For given instance resolves and sets properties and fields.
        /// It respects <see cref="DryIoc.Rules.PropertiesAndFields"/> rules set per container, 
        /// or if rules are not set it uses <see cref="PropertiesAndFields.PublicNonPrimitive"/>, 
        /// or you can specify your own rules with <paramref name="propertiesAndFields"/> parameter.</summary>
        /// <typeparam name="TService">Input and returned instance type.</typeparam>
        /// <param name="resolver">Usually a container instance, cause <see cref="Container"/> implements <see cref="IResolver"/></param>
        /// <param name="instance">Service instance with properties to resolve and initialize.</param>
        /// <param name="propertiesAndFields">(optional) Function to select properties and fields, overrides all other rules if specified.</param>
        /// <returns>Input instance with resolved dependencies, to enable fluent method composition.</returns>
        /// <remarks>Different Rules could be combined together using <see cref="PropertiesAndFields.OverrideWith"/> method.</remarks>        
        public static TService ResolvePropertiesAndFields<TService>(this IResolver resolver,
            TService instance, PropertiesAndFieldsSelector propertiesAndFields = null)
        {
            resolver.ResolvePropertiesAndFields(instance, propertiesAndFields, null);
            return instance;
        }

        /// <summary>Creates service using container for injecting parameters without registering anything.</summary>
        /// <param name="container">Container to use for type creation and injecting its dependencies.</param>
        /// <param name="concreteType">Type to instantiate.</param>
        /// <param name="with">(optional) Injection rules to select constructor/factory method, inject parameters, properties and fields.</param>
        /// <returns>Object instantiated by constructor or object returned by factory method.</returns>
        public static object New(this IContainer container, Type concreteType, InjectionRules with = null)
        {
            concreteType.ThrowIfNull().ThrowIf(concreteType.IsOpenGeneric(), Error.UNABLE_TO_NEW_OPEN_GENERIC);
            var factory = new ReflectionFactory(concreteType, null, with, Setup.With(cacheFactoryExpression: false));
            factory.BeforeRegistrationCheck(container, concreteType, null);
            var request = container.EmptyRequest.Push(ServiceInfo.Of(concreteType)).ResolveWithFactory(factory);
            var factoryDelegate = factory.GetDelegateOrDefault(request);
            var service = factoryDelegate(container.ResolutionStateCache.Items, container.ContainerWeakRef, null);
            return service;
        }

        /// <summary>Creates service using container for injecting parameters without registering anything.</summary>
        /// <typeparam name="T">Type to instantiate.</typeparam>
        /// <param name="container">Container to use for type creation and injecting its dependencies.</param>
        /// <param name="with">(optional) Injection rules to select constructor/factory method, inject parameters, properties and fields.</param>
        /// <returns>Object instantiated by constructor or object returned by factory method.</returns>
        public static T New<T>(this IContainer container, InjectionRules with = null)
        {
            return (T)container.New(typeof(T), with);
        }
    }

    /// <summary>Specifies result of <see cref="Resolver.ResolveMany{TService}"/>: either dynamic(lazy) or fixed view.</summary>
    public enum ResolveManyBehavior
    {
        /// <summary>Lazy/dynamic item resolve.</summary>
        EachItemLazyResolved,
        /// <summary>Fixed array of item at time of resolve, newly registered/removed services won't be listed.</summary>
        AllItemsResolvedIntoFixedArray
    }

    /// <summary>Provides information required for service resolution: service type, 
    /// and optional <see cref="ServiceInfoDetails"/>: key, what to do if service unresolved, and required service type.</summary>
    public interface IServiceInfo
    {
        /// <summary>The required piece of info: service type.</summary>
        Type ServiceType { get; }

        /// <summary>Additional optional details: service key, if-unresolved policy, required service type.</summary>
        ServiceInfoDetails Details { get; }

        /// <summary>Creates info from service type and details.</summary>
        /// <param name="serviceType">Required service type.</param> <param name="details">Optional details.</param> <returns>Create info.</returns>
        IServiceInfo Create(Type serviceType, ServiceInfoDetails details);
    }

    /// <summary>Provides optional service resolution details: service key, required service type, what return when service is unresolved,
    /// default value if service is unresolved, custom service value.</summary>
    public class ServiceInfoDetails
    {
        /// <summary>Default details if not specified, use default setting values, e.g. <see cref="DryIoc.IfUnresolved.Throw"/></summary>
        public static readonly ServiceInfoDetails Default = new ServiceInfoDetails();

        /// <summary>The same as <see cref="Default"/> with only difference <see cref="IfUnresolved"/> set to <see cref="DryIoc.IfUnresolved.ReturnDefault"/>.</summary>
        public static readonly ServiceInfoDetails IfUnresolvedReturnDefault = new WithIfUnresolvedReturnDefault();

        /// <summary>Creates new DTO out of provided settings, or returns default if all settings have default value.</summary>
        /// <param name="requiredServiceType">Registered service type to search for.</param>
        /// <param name="serviceKey">Service key.</param> <param name="ifUnresolved">If unresolved policy.</param>
        /// <param name="defaultValue">Custom default value, 
        /// if specified it will automatically sets <paramref name="ifUnresolved"/> to <see cref="DryIoc.IfUnresolved.ReturnDefault"/>.</param>
        /// <returns>Created details DTO.</returns>
        public static ServiceInfoDetails Of(Type requiredServiceType = null,
            object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw, object defaultValue = null)
        {
            return ifUnresolved == IfUnresolved.Throw && defaultValue == null
                ? (requiredServiceType == null
                    ? (serviceKey == null ? Default : new WithKey(serviceKey))
                    : new WithType(requiredServiceType, serviceKey))
                : (requiredServiceType == null
                    ? (serviceKey == null && defaultValue == null
                        ? IfUnresolvedReturnDefault
                        : new WithKeyReturnDefault(serviceKey, defaultValue))
                    : new WithTypeReturnDefault(requiredServiceType, serviceKey, defaultValue));
        }

        /// <summary>Sets custom value for service. This setting is orthogonal to the rest.</summary>
        /// <param name="getValue">Delegate to return custom service value.</param>
        /// <returns>Details with custom value provider set.</returns>
        public static ServiceInfoDetails Of(Func<IResolver, object> getValue)
        {
            return new WithValue(getValue.ThrowIfNull());
        }

        /// <summary>Service type to search in registry. Should be assignable to user requested service type.</summary>
        public virtual Type RequiredServiceType { get { return null; } }

        /// <summary>Service key provided with registration.</summary>
        public virtual object ServiceKey { get { return null; } }

        /// <summary>Policy to deal with unresolved request.</summary>
        public virtual IfUnresolved IfUnresolved { get { return IfUnresolved.Throw; } }

        /// <summary>Value to use in case <see cref="IfUnresolved"/> is set to <see cref="DryIoc.IfUnresolved.ReturnDefault"/>.</summary>
        public virtual object DefaultValue { get { return null; } }

        /// <summary>Allows to get, or resolve value using passed <see cref="Request"/>.</summary>
        public virtual Func<IResolver, object> GetValue { get { return null; } }

        /// <summary>Pretty prints service details to string for debugging and errors.</summary> <returns>Details string.</returns>
        public override string ToString()
        {
            if (GetValue != null)
                return "{with custom value}";

            var s = new StringBuilder();
            if (RequiredServiceType != null)
                s.Append("{required: ").Print(RequiredServiceType); // TODO omit required when it is the same as service type.
            if (ServiceKey != null)
                (s.Length == 0 ? s.Append('{') : s.Append(", ")).Print(ServiceKey, "\"");
            if (IfUnresolved != IfUnresolved.Throw)
                (s.Length == 0 ? s.Append('{') : s.Append(", ")).Append("allow default");
            return (s.Length == 0 ? s : s.Append('}')).ToString();
        }

        #region Implementation

        private sealed class WithIfUnresolvedReturnDefault : ServiceInfoDetails
        {
            public override IfUnresolved IfUnresolved { get { return IfUnresolved.ReturnDefault; } }
        }

        private class WithValue : ServiceInfoDetails
        {
            public override Func<IResolver, object> GetValue { get { return _getValue; } }
            public WithValue(Func<IResolver, object> getValue) { _getValue = getValue; }
            private readonly Func<IResolver, object> _getValue;
        }

        private class WithKey : ServiceInfoDetails
        {
            public override object ServiceKey { get { return _serviceKey; } }
            public WithKey(object serviceKey) { _serviceKey = serviceKey; }
            private readonly object _serviceKey;
        }

        private sealed class WithKeyReturnDefault : WithKey
        {
            public override IfUnresolved IfUnresolved { get { return IfUnresolved.ReturnDefault; } }
            public override object DefaultValue { get { return _defaultValue; } }
            public WithKeyReturnDefault(object serviceKey, object defaultValue)
                : base(serviceKey) { _defaultValue = defaultValue; }
            private readonly object _defaultValue;
        }

        private class WithType : WithKey
        {
            public override Type RequiredServiceType { get { return _requiredServiceType; } }
            public WithType(Type requiredServiceType, object serviceKey)
                : base(serviceKey) { _requiredServiceType = requiredServiceType; }
            private readonly Type _requiredServiceType;
        }

        private sealed class WithTypeReturnDefault : WithType
        {
            public override IfUnresolved IfUnresolved { get { return IfUnresolved.ReturnDefault; } }
            public override object DefaultValue { get { return _defaultValue; } }
            public WithTypeReturnDefault(Type requiredServiceType, object serviceKey, object defaultValue)
                : base(requiredServiceType, serviceKey) { _defaultValue = defaultValue; }
            private readonly object _defaultValue;
        }

        #endregion
    }

    /// <summary>Contains tools for combining or propagating of <see cref="IServiceInfo"/> independent of its concrete implementations.</summary>
    public static class ServiceInfoTools
    {
        /// <summary>Combines service info with details: the main task is to combine service and required service type.</summary>
        /// <typeparam name="T">Type of <see cref="IServiceInfo"/>.</typeparam>
        /// <param name="info">Source info.</param> <param name="details">Details to combine with info.</param> 
        /// <param name="request">Owner request.</param> <returns>Original source or new combined info.</returns>
        public static T WithDetails<T>(this T info, ServiceInfoDetails details, Request request)
            where T : IServiceInfo
        {
            var serviceType = info.ServiceType;
            var wrappedServiceType = request.Container.UnwrapServiceType(serviceType);
            var requiredServiceType = details == null ? null : details.RequiredServiceType;
            if (requiredServiceType == null)
            {
                if (wrappedServiceType != serviceType &&  // it is a wrapper
                    wrappedServiceType != typeof(object)) // and wrapped type is not an object, which is least specific.
                {
                    // wrapper should always have a specific service type
                    details = details == null
                        ? ServiceInfoDetails.Of(wrappedServiceType)
                        : ServiceInfoDetails.Of(wrappedServiceType, details.ServiceKey, details.IfUnresolved);
                }
            }
            else // if required type was provided, check that it is assignable to service(wrapped)type.
            {
                wrappedServiceType.ThrowIfNotOf(requiredServiceType,
                    Error.WRAPPED_NOT_ASSIGNABLE_FROM_REQUIRED_TYPE, request);
                if (wrappedServiceType == serviceType) // if Not a wrapper, 
                {
                    serviceType = requiredServiceType; // override service type with required one
                    details = ServiceInfoDetails.Of(null, details.ServiceKey, details.IfUnresolved);
                }
            }

            return serviceType == info.ServiceType && (details == null || details == info.Details)
                ? info // if service type unchanged and details absent, or the same: return original info.
                : (T)info.Create(serviceType, details); // otherwise: create new.
        }

        /// <summary>Enables propagation/inheritance of info between dependency and its owner: 
        /// for instance <see cref="ServiceInfoDetails.RequiredServiceType"/> for wrappers.</summary>
        /// <param name="dependency">Dependency info.</param>
        /// <param name="owner">Dependency holder/owner info.</param>
        /// <param name="shouldInheritServiceKey">(optional) Self-explanatory. Usually set to true for wrapper and decorator info.</param>
        /// <returns>Either input dependency info, or new info with properties inherited from the owner.</returns>
        public static IServiceInfo InheritInfo(this IServiceInfo dependency, IServiceInfo owner, bool shouldInheritServiceKey = false)
        {
            var ownerDetails = owner.Details;
            if (ownerDetails == null || ownerDetails == ServiceInfoDetails.Default)
                return dependency;

            var dependencyDetails = dependency.Details;

            var ifUnresolved = ownerDetails.IfUnresolved == IfUnresolved.Throw
                ? dependencyDetails.IfUnresolved
                : ownerDetails.IfUnresolved;

            // Use dependency key if it's non default, otherwise and if owner is not service, the
            var serviceKey = dependencyDetails.ServiceKey == null && shouldInheritServiceKey
                ? ownerDetails.ServiceKey
                : dependencyDetails.ServiceKey;

            var serviceType = dependency.ServiceType;
            var requiredServiceType = dependencyDetails.RequiredServiceType;
            if (ownerDetails.RequiredServiceType != null)
            {
                requiredServiceType = null;
                if (ownerDetails.RequiredServiceType.IsAssignableTo(serviceType))
                    serviceType = ownerDetails.RequiredServiceType;
                else
                    requiredServiceType = ownerDetails.RequiredServiceType;
            }

            if (serviceType == dependency.ServiceType && serviceKey == dependencyDetails.ServiceKey &&
                ifUnresolved == dependencyDetails.IfUnresolved && requiredServiceType == dependencyDetails.RequiredServiceType)
                return dependency;

            return dependency.Create(serviceType, ServiceInfoDetails.Of(requiredServiceType, serviceKey, ifUnresolved));
        }

        /// <summary>Appends info string representation into provided builder.</summary>
        /// <param name="s">String builder to print to.</param> <param name="info">Info to print.</param>
        /// <returns>String builder with appended info.</returns>
        public static StringBuilder Print(this StringBuilder s, IServiceInfo info)
        {
            s.Print(info.ServiceType);
            var details = info.Details.ToString();
            return details == string.Empty ? s : s.Append(' ').Append(details);
        }
    }

    /// <summary>Represents custom or resolution root service info, there is separate representation for parameter, 
    /// property and field dependencies.</summary>
    public class ServiceInfo : IServiceInfo
    {
        /// <summary>Creates info out of provided settings</summary>
        /// <param name="serviceType">Service type</param>
        /// <param name="serviceKey">(optional) Service key.</param> 
        /// <param name="ifUnresolved">(optional) If unresolved policy. Set to Throw if not specified.</param>
        /// <returns>Created info.</returns>
        public static ServiceInfo Of(Type serviceType, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            serviceType.ThrowIfNull().ThrowIf(serviceType.IsOpenGeneric(), Error.EXPECTED_CLOSED_GENERIC_SERVICE_TYPE);
            return serviceKey == null && ifUnresolved == IfUnresolved.Throw
                ? new ServiceInfo(serviceType)
                : new WithDetails(serviceType, ServiceInfoDetails.Of(null, serviceKey, ifUnresolved));
        }

        /// <summary>Type of service to create. Indicates registered service in registry.</summary>
        public Type ServiceType { get; private set; }

        /// <summary>Additional settings. If not specified uses <see cref="ServiceInfoDetails.Default"/>.</summary>
        public virtual ServiceInfoDetails Details { get { return ServiceInfoDetails.Default; } }

        /// <summary>Creates info from service type and details.</summary>
        /// <param name="serviceType">Required service type.</param> <param name="details">Optional details.</param> <returns>Create info.</returns>
        public IServiceInfo Create(Type serviceType, ServiceInfoDetails details)
        {
            return details == ServiceInfoDetails.Default
                ? new ServiceInfo(serviceType)
                : new WithDetails(serviceType, details);
        }

        /// <summary>Prints info to string using <see cref="ServiceInfoTools.Print"/>.</summary> <returns>Printed string.</returns>
        public override string ToString()
        {
            return new StringBuilder().Print(this).ToString();
        }

        #region Implementation

        private ServiceInfo(Type serviceType)
        {
            ServiceType = serviceType;
        }

        private sealed class WithDetails : ServiceInfo
        {
            public override ServiceInfoDetails Details
            {
                get { return _details; }
            }

            public WithDetails(Type serviceType, ServiceInfoDetails details)
                : base(serviceType)
            {
                _details = details;
            }

            private readonly ServiceInfoDetails _details;
        }

        #endregion
    }

    /// <summary>Provides <see cref="IServiceInfo"/> for parameter, 
    /// by default using parameter name as <see cref="IServiceInfo.ServiceType"/>.</summary>
    /// <remarks>For parameter default setting <see cref="ServiceInfoDetails.IfUnresolved"/> is <see cref="IfUnresolved.Throw"/>.</remarks>
    public class ParameterServiceInfo : IServiceInfo
    {
        /// <summary>Creates service info from parameter alone, setting service type to parameter type,
        /// and setting resolution policy to <see cref="IfUnresolved.ReturnDefault"/> if parameter is optional.</summary>
        /// <param name="parameter">Parameter to create info for.</param>
        /// <returns>Parameter service info.</returns>
        public static ParameterServiceInfo Of(ParameterInfo parameter)
        {
            parameter.ThrowIfNull();

            var isOptional = parameter.IsOptional;
            var defaultValue = isOptional ? parameter.DefaultValue : null;
            var hasDefaultValue = defaultValue != null && parameter.ParameterType.IsTypeOf(defaultValue);

            return !isOptional ? new ParameterServiceInfo(parameter)
                : new WithDetails(parameter, !hasDefaultValue
                    ? ServiceInfoDetails.IfUnresolvedReturnDefault
                    : ServiceInfoDetails.Of(ifUnresolved: IfUnresolved.ReturnDefault, defaultValue: defaultValue));
        }

        /// <summary>Service type specified by <see cref="ParameterInfo.ParameterType"/>.</summary>
        public virtual Type ServiceType { get { return _parameter.ParameterType; } }

        /// <summary>Optional service details.</summary>
        public virtual ServiceInfoDetails Details { get { return ServiceInfoDetails.Default; } }

        /// <summary>Creates info from service type and details.</summary>
        /// <param name="serviceType">Required service type.</param> <param name="details">Optional details.</param> <returns>Create info.</returns>
        public IServiceInfo Create(Type serviceType, ServiceInfoDetails details)
        {
            return serviceType == ServiceType
                ? new WithDetails(_parameter, details)
                : new TypeWithDetails(_parameter, serviceType, details);
        }

        /// <summary>Prints info to string using <see cref="ServiceInfoTools.Print"/>.</summary> <returns>Printed string.</returns>
        public override string ToString()
        {
            return new StringBuilder().Print(this).Append(" as parameter ").Print(_parameter.Name, "\"").ToString();
        }

        #region Implementation

        private readonly ParameterInfo _parameter;

        private ParameterServiceInfo(ParameterInfo parameter) { _parameter = parameter; }

        private class WithDetails : ParameterServiceInfo
        {
            public override ServiceInfoDetails Details { get { return _details; } }
            public WithDetails(ParameterInfo parameter, ServiceInfoDetails details)
                : base(parameter) { _details = details; }
            private readonly ServiceInfoDetails _details;
        }

        private sealed class TypeWithDetails : WithDetails
        {
            public override Type ServiceType { get { return _serviceType; } }
            public TypeWithDetails(ParameterInfo parameter, Type serviceType, ServiceInfoDetails details)
                : base(parameter, details) { _serviceType = serviceType; }
            private readonly Type _serviceType;
        }

        #endregion
    }

    /// <summary>Base class for property and field dependency info.</summary>
    public abstract class PropertyOrFieldServiceInfo : IServiceInfo
    {
        /// <summary>Create member info out of provide property or field.</summary>
        /// <param name="member">Member is either property or field.</param> <returns>Created info.</returns>
        public static PropertyOrFieldServiceInfo Of(MemberInfo member)
        {
            return member.ThrowIfNull() is PropertyInfo ? (PropertyOrFieldServiceInfo)
                new Property((PropertyInfo)member) : new Field((FieldInfo)member);
        }

        /// <summary>The required service type. It will be either <see cref="FieldInfo.FieldType"/> or <see cref="PropertyInfo.PropertyType"/>.</summary>
        public abstract Type ServiceType { get; }

        /// <summary>Optional details: service key, if-unresolved policy, required service type.</summary>
        public virtual ServiceInfoDetails Details { get { return ServiceInfoDetails.IfUnresolvedReturnDefault; } }

        /// <summary>Creates info from service type and details.</summary>
        /// <param name="serviceType">Required service type.</param> <param name="details">Optional details.</param> <returns>Create info.</returns>
        public abstract IServiceInfo Create(Type serviceType, ServiceInfoDetails details);

        /// <summary>Either <see cref="PropertyInfo"/> or <see cref="FieldInfo"/>.</summary>
        public abstract MemberInfo Member { get; }

        /// <summary>Sets property or field value on provided holder object.</summary>
        /// <param name="holder">Holder of property or field.</param> <param name="value">Value to set.</param>
        public abstract void SetValue(object holder, object value);

        #region Implementation

        private class Property : PropertyOrFieldServiceInfo
        {
            public override Type ServiceType { get { return _property.PropertyType; } }
            public override IServiceInfo Create(Type serviceType, ServiceInfoDetails details)
            {
                return serviceType == ServiceType
                    ? new WithDetails(_property, details)
                    : new TypeWithDetails(_property, serviceType, details);
            }

            public override MemberInfo Member { get { return _property; } }
            public override void SetValue(object holder, object value)
            {
                _property.SetValue(holder, value, null);
            }

            public override string ToString()
            {
                return new StringBuilder().Print(this).Append(" as property ").Print(_property.Name, "\"").ToString();
            }

            private readonly PropertyInfo _property;
            public Property(PropertyInfo property)
            {
                _property = property;
            }

            private class WithDetails : Property
            {
                public override ServiceInfoDetails Details { get { return _details; } }
                public WithDetails(PropertyInfo property, ServiceInfoDetails details)
                    : base(property) { _details = details; }
                private readonly ServiceInfoDetails _details;
            }

            private sealed class TypeWithDetails : WithDetails
            {
                public override Type ServiceType { get { return _serviceType; } }
                public TypeWithDetails(PropertyInfo property, Type serviceType, ServiceInfoDetails details)
                    : base(property, details) { _serviceType = serviceType; }
                private readonly Type _serviceType;
            }
        }

        private class Field : PropertyOrFieldServiceInfo
        {
            public override Type ServiceType { get { return _field.FieldType; } }
            public override IServiceInfo Create(Type serviceType, ServiceInfoDetails details)
            {
                return serviceType == null
                    ? new WithDetails(_field, details)
                    : new TypeWithDetails(_field, serviceType, details);
            }

            public override MemberInfo Member { get { return _field; } }
            public override void SetValue(object holder, object value)
            {
                _field.SetValue(holder, value);
            }

            public override string ToString()
            {
                return new StringBuilder().Print(this).Append(" as field ").Print(_field.Name, "\"").ToString();
            }

            private readonly FieldInfo _field;
            public Field(FieldInfo field)
            {
                _field = field;
            }

            private class WithDetails : Field
            {
                public override ServiceInfoDetails Details { get { return _details; } }
                public WithDetails(FieldInfo field, ServiceInfoDetails details)
                    : base(field) { _details = details; }
                private readonly ServiceInfoDetails _details;
            }

            private sealed class TypeWithDetails : WithDetails
            {
                public override Type ServiceType { get { return _serviceType; } }
                public TypeWithDetails(FieldInfo field, Type serviceType, ServiceInfoDetails details)
                    : base(field, details) { _serviceType = serviceType; }
                private readonly Type _serviceType;
            }
        }

        #endregion
    }

    /// <summary>Contains resolution stack with information about resolved service and factory for it,
    /// Additionally request is playing role of resolution context, containing <see cref="ResolutionStateCache"/>, and
    /// weak reference to <see cref="IContainer"/>. That the all required information for resolving services.
    /// Request implements <see cref="IResolver"/> interface on top of provided container, which could be use by delegate factories.</summary>
    public sealed class Request : IResolver
    {
        /// <summary>Creates empty request associated with provided <paramref name="container"/>.
        /// Every resolution will start from this request by pushing service information into, and then resolving it.</summary>
        /// <param name="container">Reference to container issued the request. Could be changed later with <see cref="SwitchContainer"/> method.</param>
        /// <param name="resolutionStatCache">Separate reference to resolution cache. It is separate because container may be
        /// switched independently in during request resolution in container parent-child setup.</param>
        /// <returns>New empty request.</returns>
        internal static Request CreateEmpty(ContainerWeakRef container, WeakReference resolutionStatCache)
        {
            return new Request(null, container, resolutionStatCache, null, null, null);
        }

        /// <summary>Indicates that request is empty initial request: there is no <see cref="ServiceInfo"/> in such a request.</summary>
        public bool IsEmpty
        {
            get { return ServiceInfo == null; }
        }

        #region Resolver

        public object ResolveDefault(Type serviceType, IfUnresolved ifUnresolved, Request _)
        {
            return Container.ResolveDefault(serviceType, ifUnresolved, this);
        }

        public object ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved, Type requiredServiceType, Request _)
        {
            return Container.ResolveKeyed(serviceType, serviceKey, ifUnresolved, requiredServiceType, this);
        }

        public void ResolvePropertiesAndFields(object instance, PropertiesAndFieldsSelector selectPropertiesAndFields, Request _)
        {
            Container.ResolvePropertiesAndFields(instance, selectPropertiesAndFields, this);
        }

        public IEnumerable<object> ResolveMany(Type serviceType, object serviceKey, Type requiredServiceType, object compositeParentKey)
        {
            return Container.ResolveMany(serviceType, serviceKey, requiredServiceType, compositeParentKey /*, this*/);
        }

        #endregion

        /// <summary>Reference to resolved items and cached factory expressions. 
        /// Used to propagate the state from resolution root, probably from another container (request creator).</summary>
        public ResolutionStateCache StateCache
        {
            get { return (_stateCacheWeakRef.Target as ResolutionStateCache).ThrowIfNull(Error.CONTAINER_IS_GARBAGE_COLLECTED); }
        }

        /// <summary>Previous request in dependency chain. It <see cref="IsEmpty"/> for resolution root.</summary>
        public readonly Request Parent;

        /// <summary>Requested service id info and commanded resolution behavior.</summary>
        public readonly IServiceInfo ServiceInfo;

        /// <summary>Factory found in container to resolve this request.</summary>
        public readonly Factory ResolvedFactory;

        /// <summary>List of specified arguments to use instead of resolving them.</summary>
        public readonly KV<bool[], ParameterExpression[]> FuncArgs;

        /// <summary>Weak reference to container.</summary>
        public readonly ContainerWeakRef ContainerWeakRef;

        /// <summary>Provides access to container currently bound to request. 
        /// By default it is container initiated request by calling resolve method,
        /// but could be changed along the way: for instance when resolving from parent container.</summary>
        public IContainer Container { get { return ContainerWeakRef.GetTarget(); } }

        /// <summary>Shortcut access to <see cref="IServiceInfo.ServiceType"/>.</summary>
        public Type ServiceType { get { return ServiceInfo == null ? null : ServiceInfo.ServiceType; } }

        /// <summary>Shortcut access to <see cref="ServiceInfoDetails.ServiceKey"/>.</summary>
        public object ServiceKey { get { return ServiceInfo.ThrowIfNull().Details.ServiceKey; } }

        /// <summary>Shortcut access to <see cref="ServiceInfoDetails.IfUnresolved"/>.</summary>
        public IfUnresolved IfUnresolved { get { return ServiceInfo.ThrowIfNull().Details.IfUnresolved; } }

        /// <summary>Shortcut access to <see cref="ServiceInfoDetails.RequiredServiceType"/>.</summary>
        public Type RequiredServiceType { get { return ServiceInfo.ThrowIfNull().Details.RequiredServiceType; } }

        /// <summary>Implementation type of factory, if request was <see cref="ResolveWithFactory"/> factory, or null otherwise.</summary>
        public Type ImplementationType
        {
            get { return ResolvedFactory == null ? null : ResolvedFactory.ImplementationType; }
        }

        /// <summary>Creates new request with provided info, and attaches current request as new request parent.</summary>
        /// <param name="info">Info about service to resolve.</param>
        /// <returns>New request for provided info.</returns>
        /// <remarks>Current request should be resolved to factory (<see cref="ResolveWithFactory"/>), before pushing info into it.</remarks>
        public Request Push(IServiceInfo info)
        {
            if (IsEmpty)
                return new Request(this, ContainerWeakRef, _stateCacheWeakRef, new Ref<IScope>(), info.ThrowIfNull(), null);

            ResolvedFactory.ThrowIfNull(Error.PUSHING_TO_REQUEST_WITHOUT_FACTORY, info.ThrowIfNull(), this);
            FactorySetup ownerSetup = ResolvedFactory.Setup;
            var inheritedInfo = info.InheritInfo(ServiceInfo, ownerSetup.FactoryType != FactoryType.Service);
            return new Request(this, ContainerWeakRef, _stateCacheWeakRef, _scope, inheritedInfo, null, FuncArgs);
        }

        /// <summary>Composes service description into <see cref="IServiceInfo"/> and calls <see cref="Push(DryIoc.IServiceInfo)"/>.</summary>
        /// <param name="serviceType">Service type to resolve.</param>
        /// <param name="serviceKey">(optional) Service key to resolve.</param>
        /// <param name="ifUnresolved">(optional) Instructs how to handle unresolved service.</param>
        /// <param name="requiredServiceType">(optional) Registered/unwrapped service type to find.</param>
        /// <returns>New request with provided info.</returns>
        public Request Push(Type serviceType,
            object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw, Type requiredServiceType = null)
        {
            var details = ServiceInfoDetails.Of(requiredServiceType, serviceKey, ifUnresolved);
            return Push(DryIoc.ServiceInfo.Of(serviceType).WithDetails(details, this));
        }

        /// <summary>Allow to switch current service info to new one: for instance it is used be decorators.</summary>
        /// <param name="getInfo">Gets new info to switch to.</param>
        /// <returns>New request with new info but the rest intact: e.g. <see cref="ResolvedFactory"/>.</returns>
        public Request UpdateServiceInfo(Func<IServiceInfo, IServiceInfo> getInfo)
        {
            return new Request(Parent, ContainerWeakRef, _stateCacheWeakRef, _scope, getInfo(ServiceInfo), ResolvedFactory, FuncArgs);
        }

        /// <summary>Returns new request with parameter expressions created for <paramref name="funcType"/> input arguments.
        /// The expression is set to <see cref="FuncArgs"/> request field to use for <see cref="WrappersSupport.FuncTypes"/>
        /// resolution.</summary>
        /// <param name="funcType">Func type to get input arguments from.</param>
        /// <returns>New request with <see cref="FuncArgs"/> field set.</returns>
        public Request WithFuncArgs(Type funcType)
        {
            var funcArgs = funcType.ThrowIf(!funcType.IsFuncWithArgs()).GetGenericParamsAndArgs();
            var funcArgExprs = new ParameterExpression[funcArgs.Length - 1];
            for (var i = 0; i < funcArgExprs.Length; i++)
            {
                var funcArg = funcArgs[i];
                funcArgExprs[i] = Expression.Parameter(funcArg, funcArg.Name + i);
            }

            var isArgUsed = new bool[funcArgExprs.Length];
            var funcArgExpr = new KV<bool[], ParameterExpression[]>(isArgUsed, funcArgExprs);
            return new Request(Parent, ContainerWeakRef, _stateCacheWeakRef, _scope, ServiceInfo, ResolvedFactory, funcArgExpr);
        }

        /// <summary>Changes container to passed one. Could be used by child container, 
        /// to switch child container to parent preserving the rest of request state.</summary>
        /// <param name="containerWeakRef">Reference to container to switch to.</param>
        /// <returns>Request with replaced container.</returns>
        public Request SwitchContainer(ContainerWeakRef containerWeakRef)
        {
            return new Request(Parent, containerWeakRef, _stateCacheWeakRef, _scope, ServiceInfo, ResolvedFactory, FuncArgs);
        }

        /// <summary>Returns new request with set <see cref="ResolvedFactory"/>.</summary>
        /// <param name="factory">Factory to which request is resolved.</param>
        /// <returns>New request with set factory.</returns>
        public Request ResolveWithFactory(Factory factory)
        {
            if (IsEmpty || (ResolvedFactory != null && ResolvedFactory.FactoryID == factory.FactoryID))
                return this; // resolving only once, no need to check recursion again.



            if (factory.FactoryType == FactoryType.Service)
                for (var p = Parent; !p.IsEmpty; p = p.Parent)
                    Throw.If(p.ResolvedFactory.FactoryID == factory.FactoryID,
                        Error.RECURSIVE_DEPENDENCY_DETECTED, Print(factory.FactoryID));

            return new Request(Parent, ContainerWeakRef, _stateCacheWeakRef, _scope, ServiceInfo, factory, FuncArgs);
        }

        /// <summary>Searches parent request stack upward and returns closest parent of <see cref="FactoryType.Service"/>.
        /// If not found returns <see cref="IsEmpty"/> request.</summary>
        /// <returns>Return closest <see cref="FactoryType.Service"/> parent or root.</returns>
        public Request GetNonWrapperParentOrEmpty()
        {
            var p = Parent;
            while (!p.IsEmpty && p.ResolvedFactory.FactoryType == FactoryType.Wrapper)
                p = p.Parent;
            return p;
        }

        /// <summary>Enumerates all request stack parents. Last returned will <see cref="IsEmpty"/> empty parent.</summary>
        /// <returns>Unfolding parents.</returns>
        public IEnumerable<Request> Enumerate()
        {
            for (var r = this; !r.IsEmpty; r = r.Parent)
                yield return r;
        }

        /// <summary>Prints current request info only (no parents printed) to provided builder.</summary>
        /// <param name="s">Builder to print too.</param>
        /// <returns>(optional) Builder to appended info to, or new builder if not specified.</returns>
        public StringBuilder PrintCurrent(StringBuilder s = null)
        {
            s = s ?? new StringBuilder();
            if (IsEmpty) return s.Append("<empty>");
            if (ResolvedFactory != null && ResolvedFactory.FactoryType != FactoryType.Service)
                s.Append(ResolvedFactory.FactoryType.ToString().ToLower()).Append(' ');
            if (ImplementationType != null && ImplementationType != ServiceType)
                s.Print(ImplementationType).Append(": ");
            return s.Append(ServiceInfo);
        }

        /// <summary>Prints full stack of requests starting from current one using <see cref="PrintCurrent"/>.</summary>
        /// <param name="recursiveFactoryID">Flag specifying that in case of found recursion/repetition of requests, 
        /// mark repeated requests.</param>
        /// <returns>Builder with appended request stack info.</returns>
        public StringBuilder Print(int recursiveFactoryID = -1)
        {
            var s = PrintCurrent(new StringBuilder());
            if (Parent == null)
                return s;

            s = recursiveFactoryID == -1 ? s : s.Append(" <--recursive");
            return Parent.Enumerate().TakeWhile(r => !r.IsEmpty).Aggregate(s, (a, r) =>
            {
                a = r.PrintCurrent(a.AppendLine().Append(" in "));
                return r.ResolvedFactory.FactoryID == recursiveFactoryID ? a.Append(" <--recursive") : a;
            });
        }

        /// <summary>Print while request stack info to string using <seealso cref="Print"/>.</summary>
        /// <returns>String with info.</returns>
        public override string ToString()
        {
            return Print().ToString();
        }

        #region Implementation

        internal Request(Request parent,
            ContainerWeakRef containerWeakRef, WeakReference stateCacheWeakRef,
            Ref<IScope> scope, IServiceInfo serviceInfo, Factory resolvedFactory,
            KV<bool[], ParameterExpression[]> funcArgs = null)
        {
            Parent = parent;
            ContainerWeakRef = containerWeakRef;
            _stateCacheWeakRef = stateCacheWeakRef;
            _scope = scope;
            ServiceInfo = serviceInfo;
            ResolvedFactory = resolvedFactory;
            FuncArgs = funcArgs;
        }

        private readonly WeakReference _stateCacheWeakRef;
        private readonly Ref<IScope> _scope;

        #endregion
    }

    /// <summary>Type of services supported by Container.</summary>
    public enum FactoryType
    {
        /// <summary>(default) Defines normal service factory</summary>
        Service,
        /// <summary>Defines decorator factory</summary>
        Decorator,
        /// <summary>Defines wrapper factory.</summary>
        Wrapper
    };

    /// <summary>Base class to store optional <see cref="Factory"/> settings.</summary>
    public abstract class FactorySetup
    {
        /// <summary>Factory type is required to be specified by concrete setups as in 
        /// <see cref="Setup"/>, <see cref="SetupDecorator"/>, <see cref="SetupWrapper"/>.</summary>
        public abstract FactoryType FactoryType { get; }

        /// <summary>Set to true allows to cache and use cached factored service expression.</summary>
        public virtual bool CacheFactoryExpression { get { return false; } }

        /// <summary>Arbitrary metadata object associated with Factory/Implementation.</summary>
        public virtual object Metadata { get { return null; } }

        // TODO: Use it for Service factory. Currently used by Decorator only.
        /// <summary>Predicate to check if factory could be used for resolved request.</summary>
        public virtual Func<Request, bool> Condition { get { return null; } }

        /// <summary>Specifies how to wrap the reused/shared instance to apply additional behavior, e.g. <see cref="WeakReference"/>, 
        /// or disable disposing with <see cref="ReuseHiddenDisposable"/>, etc.</summary>
        public virtual Type[] ReuseWrappers { get { return null; } }
    }

    /// <summary>Setup for <see cref="DryIoc.FactoryType.Service"/> factory.</summary>
    public class Setup : FactorySetup
    {
        /// <summary>Default setup for service factories.</summary>
        public static readonly Setup Default = new Setup();

        /// <summary>Constructs setup object out of specified settings. If all settings are default then <see cref="Default"/> setup will be returned.</summary>
        /// <param name="cacheFactoryExpression">(optional)</param>
        /// <param name="reuseWrappers">(optional) Multiple reuse wrappers.</param>
        /// <param name="lazyMetadata">(optional)</param> <param name="metadata">(optional) Overrides <paramref name="lazyMetadata"/></param>
        /// <returns>New setup object or <see cref="Default"/>.</returns>
        public static Setup With(bool cacheFactoryExpression = true,
            Func<object> lazyMetadata = null, object metadata = null,
            params Type[] reuseWrappers)
        {
            if (cacheFactoryExpression && reuseWrappers == null && lazyMetadata == null && metadata == null)
                return Default;

            if (!reuseWrappers.IsNullOrEmpty())
            {
                var indexOfNotReused = reuseWrappers.IndexOf(t => !t.IsAssignableTo(typeof(IReuseWrapper)));
                Throw.If(indexOfNotReused != -1, Error.REG_REUSED_OBJ_WRAPPER_IS_NOT_IREUSED, indexOfNotReused, reuseWrappers, typeof(IReuseWrapper));
            }

            return new Setup(cacheFactoryExpression, lazyMetadata, metadata, reuseWrappers);
        }

        /// <summary>Default factory type is for service factory.</summary>
        public override FactoryType FactoryType { get { return FactoryType.Service; } }

        /// <summary>Set to true allows to cache and use cached factored service expression.</summary>
        public override bool CacheFactoryExpression { get { return _cacheFactoryExpression; } }

        /// <summary>Specifies how to wrap the reused/shared instance to apply additional behavior, e.g. <see cref="WeakReference"/>, 
        /// or disable disposing with <see cref="ReuseHiddenDisposable"/>, etc.</summary>
        public override Type[] ReuseWrappers { get { return _reuseWrappers; } }

        /// <summary>Arbitrary metadata object associated with Factory/Implementation.</summary>
        public override object Metadata
        {
            get { return _metadata ?? (_metadata = _lazyMetadata == null ? null : _lazyMetadata()); }
        }

        #region Implementation

        private Setup(bool cacheFactoryExpression = true,
            Func<object> lazyMetadata = null, object metadata = null,
            Type[] reuseWrappers = null)
        {
            _cacheFactoryExpression = cacheFactoryExpression;
            _lazyMetadata = lazyMetadata;
            _metadata = metadata;
            _reuseWrappers = reuseWrappers;
        }

        private readonly bool _cacheFactoryExpression;
        private readonly Func<object> _lazyMetadata;
        private object _metadata;
        private readonly Type[] _reuseWrappers;

        #endregion
    }

    /// <summary>Setup for <see cref="DryIoc.FactoryType.Wrapper"/> factory.</summary>
    public class SetupWrapper : FactorySetup
    {
        /// <summary>Default setup which will look for wrapped service type as single generic parameter.</summary>
        public static readonly SetupWrapper Default = new SetupWrapper();

        /// <summary>Returns <see cref="DryIoc.FactoryType.Wrapper"/> type.</summary>
        public override FactoryType FactoryType { get { return FactoryType.Wrapper; } }

        /// <summary>(optional) Tool for wrapping and unwrapping reused object.</summary>
        public readonly IReuseWrapperFactory ReuseWrapperFactory;

        /// <summary>Delegate to get wrapped type from provided wrapper type. 
        /// If wrapper is generic, then wrapped type is usually a generic parameter.</summary>
        public readonly Func<Type, Type> UnwrapServiceType;

        /// <summary>Creates setup with all settings specified. If all is omitted: then <see cref="Default"/> will be used.</summary>
        /// <param name="unwrapServiceType">Wrapped service selector rule.</param>
        /// <param name="reuseWrapperFactory"></param>
        /// <returns>New setup with non-default settings or <see cref="Default"/> otherwise.</returns>
        public static SetupWrapper With(Func<Type, Type> unwrapServiceType = null, IReuseWrapperFactory reuseWrapperFactory = null)
        {
            return unwrapServiceType == null && reuseWrapperFactory == null
                ? Default : new SetupWrapper(unwrapServiceType, reuseWrapperFactory);
        }

        #region Implementation

        private SetupWrapper(Func<Type, Type> getWrappedServiceType = null, IReuseWrapperFactory reuseWrapperFactory = null)
        {
            UnwrapServiceType = getWrappedServiceType ?? GetSingleGenericArgByDefault;
            ReuseWrapperFactory = reuseWrapperFactory;
        }

        private static Type GetSingleGenericArgByDefault(Type wrapperType)
        {
            wrapperType.ThrowIf(!wrapperType.IsClosedGeneric(),
                Error.NON_GENERIC_WRAPPER_NO_WRAPPED_TYPE_SPECIFIED);

            var typeArgs = wrapperType.GetGenericParamsAndArgs();
            Throw.If(typeArgs.Length != 1, Error.WRAPPER_CAN_WRAP_SINGLE_SERVICE_ONLY, wrapperType);
            return typeArgs[0];
        }

        #endregion
    }

    /// <summary>Setup for <see cref="DryIoc.FactoryType.Decorator"/> factory.
    /// By default decorator is applied to service type it registered with. Or you can provide specific <see cref="Condition"/>.</summary>
    public class SetupDecorator : FactorySetup
    {
        /// <summary>Default decorator setup: decorator is applied to service type it registered with.</summary>
        public static readonly SetupDecorator Default = new SetupDecorator();

        /// <summary>Creates setup with optional condition.</summary>
        /// <param name="condition">(optional)</param> <returns>New setup with condition or <see cref="Default"/>.</returns>
        public static SetupDecorator With(Func<Request, bool> condition = null)
        {
            return condition == null ? Default : new SetupDecorator(condition);
        }

        /// <summary>Returns <see cref="DryIoc.FactoryType.Decorator"/> factory type.</summary>
        public override FactoryType FactoryType { get { return FactoryType.Decorator; } }

        /// <summary>Predicate to check if request is fine to apply decorator for resolved service.</summary>
        public override Func<Request, bool> Condition
        {
            get { return _condition; }
        }

        #region Implementation

        private readonly Func<Request, bool> _condition;

        private SetupDecorator(Func<Request, bool> condition = null)
        {
            _condition = condition ?? (_ => true);
        }

        #endregion
    }

    /// <summary>Facility for creating concrete factories from some template/prototype. Example: 
    /// creating closed-generic type reflection factory from registered open-generic prototype factory.</summary>
    public interface IConcreteFactoryProvider
    {
        /// <summary>Returns factories created by <see cref="ProvideConcreteFactory"/> so far.</summary>
        IEnumerable<KV<Type, object>> ProvidedFactoriesServiceTypeKey { get; }

        /// <summary>Method applied for factory provider, returns new factory per request.</summary>
        /// <param name="request">Request to resolve.</param> <returns>Returns new factory per request.</returns>
        Factory ProvideConcreteFactory(Request request);
    }

    /// <summary>Base class for different ways to instantiate service: 
    /// <list type="bullet">
    /// <item>Through reflection - <see cref="ReflectionFactory"/></item>
    /// <item>Using custom delegate - <see cref="DelegateFactory"/></item>
    /// <item>Using custom expression - <see cref="ExpressionFactory"/></item>
    /// <item>Wraps externally created instance - <see cref="InstanceFactory"/></item>
    /// </list>
    /// For all of the types Factory should provide result as <see cref="Expression"/> and <see cref="FactoryDelegate"/>.
    /// Factories are supposed to be immutable as the results Cache is handled separately by <see cref="ResolutionStateCache"/>.
    /// Each created factory has an unique ID set in <see cref="FactoryID"/>.</summary>
    public abstract class Factory
    {
        /// <summary>Unique factory id generated from static seed.</summary>
        public int FactoryID { get; private set; }

        /// <summary>Reuse policy for factory created services.</summary>
        public readonly IReuse Reuse;

        /// <summary>Setup may contain different/non-default factory settings.</summary>
        public FactorySetup Setup
        {
            get { return _setup; }
            protected internal set { _setup = value ?? DryIoc.Setup.Default; }
        }

        /// <summary>Shortcut for <see cref="FactorySetup.FactoryType"/>.</summary>
        public FactoryType FactoryType
        {
            get { return Setup.FactoryType; }
        }

        /// <summary>Non-abstract closed service implementation type. 
        /// May be null in such factories as <see cref="DelegateFactory"/>, where it could not be determined
        /// until delegate is invoked.</summary>
        public virtual Type ImplementationType { get { return null; } }

        /// <summary>Indicates that Factory is factory provider and 
        /// consumer should call <see cref="IConcreteFactoryProvider.ProvideConcreteFactory"/>  to get concrete factory.</summary>
        public virtual IConcreteFactoryProvider Provider { get { return null; } }

        /// <summary>Initializes reuse and setup. Sets the <see cref="FactoryID"/></summary>
        /// <param name="reuse">(optional)</param>
        /// <param name="setup">(optional)</param>
        protected Factory(IReuse reuse = null, FactorySetup setup = null)
        {
            FactoryID = Interlocked.Increment(ref _lastFactoryID);
            Reuse = reuse;
            Setup = setup ?? DryIoc.Setup.Default;
        }

        /// <summary>Validates that factory is OK for registered service type.</summary>
        /// <param name="container">Container to register factory in.</param>
        /// <param name="serviceType">Service type to register factory for.</param>
        /// <param name="serviceKey">Service key to register factory with.</param>
        public virtual void BeforeRegistrationCheck(IContainer container, Type serviceType, object serviceKey)
        {
            Throw.If(serviceType.IsGenericDefinition() && Provider == null,
                Error.REG_OPEN_GENERIC_REQUIRE_FACTORY_PROVIDER, serviceType);
        }

        /// <summary>The main factory method to create service expression, e.g. "new Client(new Service())".
        /// If <paramref name="request"/> has <see cref="Request.FuncArgs"/> specified, they could be used in expression.</summary>
        /// <param name="request">Service request.</param>
        /// <returns>Created expression.</returns>
        public abstract Expression CreateExpressionOrDefault(Request request);

        /// <summary>Returns service expression: either by creating it with <see cref="CreateExpressionOrDefault"/> or taking expression from cache.
        /// Before returning method may transform the expression  by applying <see cref="Reuse"/>, or/and decorators if found any.
        /// If <paramref name="requiredWrapperType"/> specified: result expression may be of required wrapper type.</summary>
        /// <param name="request">Request for service.</param>
        /// <param name="requiredWrapperType">(optional) Reuse wrapper type of expression to return.</param>
        /// <returns>Service expression.</returns>
        public Expression GetExpressionOrDefault(Request request, Type requiredWrapperType = null)
        {
            request = request.ResolveWithFactory(this);

            var reuseMappingRule = request.Container.Rules.ReuseMapping;
            var reuse = reuseMappingRule == null ? Reuse : reuseMappingRule(Reuse, request);

            ThrowIfReuseHasShorterLifespanThanParent(reuse, request);

            var decorator = request.Container.GetDecoratorExpressionOrDefault(request);
            var noOrFuncDecorator = decorator == null || decorator is LambdaExpression;

            var isCacheable = Setup.CacheFactoryExpression
                && noOrFuncDecorator && request.FuncArgs == null && requiredWrapperType == null;
            if (isCacheable)
            {
                var cachedServiceExpr = request.StateCache.GetCachedFactoryExpressionOrDefault(FactoryID);
                if (cachedServiceExpr != null)
                    return decorator == null ? cachedServiceExpr : Expression.Invoke(decorator, cachedServiceExpr);
            }

            var serviceExpr = noOrFuncDecorator ? CreateExpressionOrDefault(request) : decorator;
            if (serviceExpr != null && reuse != null)
            {
                // When singleton scope, and no Func in request chain, and no renewable wrapper used,
                // then reused instance could be directly inserted into delegate instead of lazy requested from Scope.
                var canBeInstantiated = reuse is SingletonReuse && request.Container.Rules.SingletonOptimization
                    && (request.Parent.IsEmpty || !request.Parent.Enumerate().Any(r => r.ServiceType.IsFunc()))
                    && Setup.ReuseWrappers.IndexOf(w => w.IsAssignableTo(typeof(IRecyclable))) == -1;

                serviceExpr = canBeInstantiated
                    ? GetInstantiatedScopedServiceExpressionOrDefault(serviceExpr, reuse, request, requiredWrapperType)
                    : GetScopedServiceExpressionOrDefault(serviceExpr, reuse, request, requiredWrapperType);
            }

            if (serviceExpr == null)
            {
                Throw.If(request.IfUnresolved == IfUnresolved.Throw, Error.UNABLE_TO_RESOLVE_SERVICE, request);
                return null;
            }

            if (isCacheable)
                request.StateCache.CacheFactoryExpression(FactoryID, serviceExpr);

            if (noOrFuncDecorator && decorator != null)
                serviceExpr = Expression.Invoke(decorator, serviceExpr);

            return serviceExpr;
        }

        /// <summary>Check method name for explanation XD.</summary> <param name="reuse">Reuse to check.</param> <param name="request">Request to resolve.</param>
        protected static void ThrowIfReuseHasShorterLifespanThanParent(IReuse reuse, Request request)
        {
            if (reuse != null && !request.Parent.IsEmpty &&
                request.Container.Rules.ThrowIfDepenedencyHasShorterReuseLifespan)
            {
                var parentReuse = request.Parent.ResolvedFactory.Reuse;
                if (parentReuse != null)
                    Throw.If(reuse.Lifespan < parentReuse.Lifespan,
                        Error.DEPENDENCY_HAS_SHORTER_REUSE_LIFESPAN, request.PrintCurrent(), request.Parent, reuse, parentReuse);
            }
        }

        /// <summary>Creates factory delegate from service expression and returns it. By default uses <see cref="FactoryCompiler"/>
        /// to compile delegate from expression but could be overridden by concrete factory type: e.g. <see cref="DelegateFactory"/></summary>
        /// <param name="request">Service request.</param>
        /// <returns>Factory delegate created from service expression.</returns>
        public virtual FactoryDelegate GetDelegateOrDefault(Request request)
        {
            var expression = GetExpressionOrDefault(request);
            return expression == null ? null : expression.CompileToDelegate(request.Container.Rules);
        }

        /// <summary>Returns nice string representation of factory.</summary>
        /// <returns>String representation.</returns>
        public override string ToString()
        {
            var s = new StringBuilder();
            s.Append("{FactoryID=").Append(FactoryID);
            if (ImplementationType != null)
                s.Append(", ImplType=").Print(ImplementationType);
            if (Reuse != null)
                s.Append(", ReuseType=").Print(Reuse.GetType());
            if (Setup.FactoryType != DryIoc.Setup.Default.FactoryType)
                s.Append(", FactoryType=").Append(Setup.FactoryType);
            return s.Append("}").ToString();
        }

        #region Implementation

        private static int _lastFactoryID;
        private FactorySetup _setup;

        private Expression GetScopedServiceExpressionOrDefault(Expression serviceExpr, IReuse reuse, Request request, Type requiredWrapperType = null)
        {
            var getScopeExpr = reuse.GetScopeExpression(Container.ResolverExpr, Container.ResolutionScopeParamExpr, request);

            var serviceType = serviceExpr.Type;
            var factoryIDExpr = Expression.Constant(FactoryID);

            var wrapperTypes = Setup.ReuseWrappers;
            if (wrapperTypes.IsNullOrEmpty())
                return Expression.Convert(Expression.Call(getScopeExpr, Scope.GetOrAddMethod,
                    factoryIDExpr, Expression.Lambda<Func<object>>(serviceExpr, null)), serviceType);

            // First wrap serviceExpr with wrapper Wrap method.
            var wrappers = new IReuseWrapperFactory[wrapperTypes.Length];
            for (var i = 0; i < wrapperTypes.Length; ++i)
            {
                var wrapperType = wrapperTypes[i];
                var wrapperFactory = request.Container.GetWrapperFactoryOrDefault(wrapperType).ThrowIfNull();
                var wrapper = ((SetupWrapper)wrapperFactory.Setup).ReuseWrapperFactory;

                serviceExpr = Expression.Call(
                    request.StateCache.GetOrAddItemExpression(wrapper, typeof(IReuseWrapperFactory)),
                    "Wrap", null, serviceExpr);

                wrappers[i] = wrapper; // save wrapper for later unwrap
            }

            // Makes call like this: scope.GetOrAdd(id, () => wrapper1.Wrap(wrapper0.Wrap(new Service)))
            var getServiceExpr = Expression.Lambda(serviceExpr, null);
            var getScopedServiceExpr = Expression.Call(getScopeExpr, Scope.GetOrAddMethod, factoryIDExpr, getServiceExpr);

            // Unwrap wrapped service in backward order like this: wrapper0.Unwrap(wrapper1.Unwrap(scope.GetOrAdd(...)))
            for (var i = wrapperTypes.Length - 1; i >= 0; --i)
            {
                var wrapperType = wrapperTypes[i];

                // Stop on required wrapper type, if provided.
                if (requiredWrapperType != null && requiredWrapperType == wrapperType)
                    return Expression.Convert(getScopedServiceExpr, requiredWrapperType);

                var wrapperExpr = request.StateCache.GetOrAddItemExpression(wrappers[i], typeof(IReuseWrapperFactory));
                getScopedServiceExpr = Expression.Call(wrapperExpr, "Unwrap", null, getScopedServiceExpr);
            }

            return requiredWrapperType != null ? null
                : Expression.Convert(getScopedServiceExpr, serviceType);
        }

        private Expression GetInstantiatedScopedServiceExpressionOrDefault(Expression serviceExpr, IReuse reuse, Request request, Type requiredWrapperType = null)
        {
            var factoryDelegate = serviceExpr.CompileToDelegate(request.Container.Rules);
            IScope ignoredResolutionScope = null;
            var scope = reuse.GetScope(request.Container, ref ignoredResolutionScope);

            var wrapperTypes = Setup.ReuseWrappers;
            var serviceType = serviceExpr.Type;
            if (wrapperTypes == null || wrapperTypes.Length == 0)
                return request.StateCache.GetOrAddItemExpression(
                    scope.GetOrAdd(FactoryID, () => factoryDelegate(request.StateCache.Items, request.ContainerWeakRef, null)),
                    serviceType);

            var wrappers = new IReuseWrapperFactory[wrapperTypes.Length];
            for (var i = 0; i < wrapperTypes.Length; ++i)
            {
                var wrapperType = wrapperTypes[i];
                var wrapperFactory = request.Container.GetWrapperFactoryOrDefault(wrapperType).ThrowIfNull();
                var wrapper = ((SetupWrapper)wrapperFactory.Setup).ReuseWrapperFactory;
                var serviceFactory = factoryDelegate;
                factoryDelegate = (st, cs, rs) => wrapper.Wrap(serviceFactory(st, cs, rs));
                wrappers[i] = wrapper;
            }

            var wrappedService = scope.GetOrAdd(FactoryID,
                () => factoryDelegate(request.StateCache.Items, request.ContainerWeakRef, null));

            for (var i = wrapperTypes.Length - 1; i >= 0; --i)
            {
                var wrapperType = wrapperTypes[i];
                if (requiredWrapperType == wrapperType)
                    return request.StateCache.GetOrAddItemExpression(wrappedService, requiredWrapperType);
                wrappedService = wrappers[i].Unwrap(wrappedService);
            }

            return requiredWrapperType != null ? null
                : request.StateCache.GetOrAddItemExpression(wrappedService, serviceType);
        }

        #endregion
    }

    /// <summary>Thin wrapper for pre-created service object registered: more lightweight then <see cref="DelegateFactory"/>,
    /// and provides type of registered instance as <see cref="ImplementationType"/></summary>
    /// <remarks>Reuse is not applied to registered object, therefore it does not saved to any Scope, 
    /// and container is not responsible for Disposing it.</remarks>
    public sealed class InstanceFactory : Factory
    {
        /// <summary>Type of wrapped instance.</summary>
        public override Type ImplementationType
        {
            get { return _instance.GetType(); }
        }

        /// <summary>Creates wrapper around provided instance, that will return it either as expression or directly for resolution root.</summary>
        /// <param name="instance">Service instance to wrap.</param> <param name="setup">(optional) Setup.</param>
        public InstanceFactory(object instance, FactorySetup setup = null)
            : base(null, setup)
        {
            _instance = instance.ThrowIfNull();
        }

        /// <summary>Throw if instance is not of registered service type.</summary>
        /// <param name="container">(ignored)</param>
        /// <param name="serviceType">Service type to register instance for.</param>
        /// <param name="serviceKey">(ignored).</param>
        public override void BeforeRegistrationCheck(IContainer container, Type serviceType, object serviceKey)
        {
            _instance.ThrowIfNotOf(serviceType, Error.REGED_OBJ_NOT_ASSIGNABLE_TO_SERVICE_TYPE, serviceType);
        }

        /// <summary>Adds instance to resolution cache and returns it wrapped in expression.</summary>
        /// <param name="request">Request to resolve.</param> <returns>Instance wrapped in expression.</returns>
        public override Expression CreateExpressionOrDefault(Request request)
        {
            return request.StateCache.GetOrAddItemExpression(_instance);
        }

        /// <summary>Returns instance as-is wrapped in <see cref="FactoryDelegate"/>. It happens when instance is directly resolved from container.</summary>
        /// <param name="_">(ignored)</param> <returns>Instance wrapped in delegate.</returns>
        public override FactoryDelegate GetDelegateOrDefault(Request _)
        {
            return (state, regRef, scope) => _instance;
        }

        private readonly object _instance;
    }

    /// <summary>Declares delegate to get single factory method or constructor for resolved request.</summary>
    /// <param name="request">Request to resolve.</param>
    /// <returns>Factory method wrapper over constructor or method.</returns>
    public delegate FactoryMethod FactoryMethodSelector(Request request);

    /// <summary>Specifies how to get parameter info for injected parameter and resolved request</summary>
    /// <remarks>Request is for parameter method owner not for parameter itself.</remarks>
    /// <param name="parameter">Parameter to inject.</param>
    /// <param name="request">Request for parameter method/constructor owner.</param>
    /// <returns>Service info describing how to inject parameter.</returns>
    public delegate ParameterServiceInfo ParameterSelector(ParameterInfo parameter, Request request);

    /// <summary>Specifies what properties or fields to inject and how.</summary>
    /// <param name="request">Request for property/field owner.</param>
    /// <returns>Corresponding service info for each property/field to be injected.</returns>
    public delegate IEnumerable<PropertyOrFieldServiceInfo> PropertiesAndFieldsSelector(Request request);

    /// <summary>Contains alternative rules to select constructor in implementation type registered with <see cref="ReflectionFactory"/>.</summary>
    public static partial class Constructor
    {
        /// <summary>Searches for constructor with all resolvable parameters or throws <see cref="ContainerException"/> if not found.
        /// Works both for resolving as service and as Func&lt;TArgs..., TService&gt;.</summary>
        public static FactoryMethodSelector WithAllResolvableArguments = request =>
        {
            var implementationType = request.ImplementationType.ThrowIfNull();
            var ctors = implementationType.GetAllConstructors().ToArrayOrSelf();
            if (ctors.Length == 0)
                return null; // Delegate handling of constructor absence to caller code.

            if (ctors.Length == 1)
                return ctors[0];

            var ctorsWithMoreParamsFirst = ctors
                .Select(c => new { Ctor = c, Params = c.GetParameters() })
                .OrderByDescending(x => x.Params.Length);

            if (request.Parent.ServiceType.IsFuncWithArgs())
            {
                // For Func with arguments, match constructor should contain all input arguments and the rest should be resolvable.
                var funcType = request.Parent.ServiceType;
                var funcArgs = funcType.GetGenericParamsAndArgs();
                var inputArgCount = funcArgs.Length - 1;

                var matchedCtor = ctorsWithMoreParamsFirst
                    .Where(x => x.Params.Length >= inputArgCount)
                    .FirstOrDefault(x =>
                    {
                        var matchedIndecesMask = 0;
                        return x.Params.Except(
                            x.Params.Where(p =>
                            {
                                var inputArgIndex = funcArgs.IndexOf(t => t == p.ParameterType);
                                if (inputArgIndex == -1 || inputArgIndex == inputArgCount ||
                                    (matchedIndecesMask & inputArgIndex << 1) != 0)
                                    // input argument was already matched by another parameter
                                    return false;
                                matchedIndecesMask |= inputArgIndex << 1;
                                return true;
                            }))
                            .All(p => ResolveParameter(p, (ReflectionFactory)request.ResolvedFactory, request) != null);
                    });

                return
                    matchedCtor.ThrowIfNull(Error.UNABLE_TO_FIND_MATCHING_CTOR_FOR_FUNC_WITH_ARGS, funcType, request)
                        .Ctor;
            }
            else
            {
                var matchedCtor = ctorsWithMoreParamsFirst.FirstOrDefault(
                    x =>
                        x.Params.All(
                            p => ResolveParameter(p, (ReflectionFactory)request.ResolvedFactory, request) != null));
                return matchedCtor.ThrowIfNull(Error.UNABLE_TO_FIND_CTOR_WITH_ALL_RESOLVABLE_ARGS, request).Ctor;
            }
        };

        #region Implementation

        private static Expression ResolveParameter(ParameterInfo p, ReflectionFactory factory, Request request)
        {
            var container = request.Container;
            var getParamInfo = container.Rules.Parameters.OverrideWith(factory.Rules.Parameters);
            var paramInfo = getParamInfo(p, request) ?? ParameterServiceInfo.Of(p);
            var paramRequest = request.Push(paramInfo.WithDetails(ServiceInfoDetails.IfUnresolvedReturnDefault, request));
            var paramFactory = container.ResolveFactory(paramRequest);
            return paramFactory == null ? null : paramFactory.GetExpressionOrDefault(paramRequest);
        }

        #endregion
    }

    /// <summary>DSL for specifying <see cref="ParameterSelector"/> injection rules.</summary>
    public static partial class Parameters
    {
        /// <summary>Specifies to return default details <see cref="ServiceInfoDetails.Default"/> for all parameters.</summary>
        public static ParameterSelector Of = (p, req) => null;

        /// <summary>Specifies that all parameters could be set to default if unresolved.</summary>
        public static ParameterSelector DefaultIfUnresolved = ((p, req) =>
            ParameterServiceInfo.Of(p).WithDetails(ServiceInfoDetails.IfUnresolvedReturnDefault, req));

        public static ParameterSelector OverrideWith(this ParameterSelector source, ParameterSelector other)
        {
            return source == null || source == Of ? other ?? Of
                : other == null || other == Of ? source
                : (p, req) => other(p, req) ?? source(p, req);
        }

        public static ParameterSelector Condition(this ParameterSelector source, Func<ParameterInfo, bool> condition,
            Type requiredServiceType = null, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw, object defaultValue = null)
        {
            return source.CombineWith(condition, ServiceInfoDetails.Of(requiredServiceType, serviceKey, ifUnresolved, defaultValue));
        }

        public static ParameterSelector Name(this ParameterSelector source, string name,
            Type requiredServiceType = null, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw, object defaultValue = null)
        {
            return source.Condition(p => p.Name.Equals(name), requiredServiceType, serviceKey, ifUnresolved, defaultValue);
        }

        public static ParameterSelector Name(this ParameterSelector source, string name, Func<IResolver, object> getValue)
        {
            return source.CombineWith(p => p.Name.Equals(name), ServiceInfoDetails.Of(getValue));
        }

        public static ParameterSelector Name(this ParameterSelector source, string name, object value)
        {
            return source.Name(name, _ => value);
        }

        public static ParameterSelector Type(this ParameterSelector source, Type type,
            Type requiredServiceType = null, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.Throw, object defaultValue = null)
        {
            type.ThrowIfNull();
            return source.Condition(p => type.IsAssignableTo(p.ParameterType), requiredServiceType, serviceKey, ifUnresolved, defaultValue);
        }

        public static ParameterSelector Type(this ParameterSelector source, Type type, Func<IResolver, object> getValue)
        {
            type.ThrowIfNull();
            return source.CombineWith(p => type.IsAssignableTo(p.ParameterType), ServiceInfoDetails.Of(getValue));
        }

        public static ParameterSelector Type(this ParameterSelector source, Type type, object value)
        {
            return source.Type(type, _ => value);
        }

        public static IEnumerable<Attribute> GetAttributes(this ParameterInfo parameter, Type attributeType = null, bool inherit = false)
        {
            return parameter.GetCustomAttributes(attributeType ?? typeof(Attribute), inherit).Cast<Attribute>();
        }

        #region Implementation

        private static ParameterSelector CombineWith(this ParameterSelector source,
            Func<ParameterInfo, bool> condition, ServiceInfoDetails details)
        {
            condition.ThrowIfNull();
            return (parameter, request) => condition(parameter)
                ? ParameterServiceInfo.Of(parameter).WithDetails(details, request)
                : source(parameter, request);
        }

        #endregion
    }

    /// <summary>DSL for specifying <see cref="PropertiesAndFieldsSelector"/> injection rules.</summary>
    public static partial class PropertiesAndFields
    {
        /// <summary>Say to not resolve any properties or fields.</summary>
        public static PropertiesAndFieldsSelector Of = request => null;

        /// <summary>Public assignable instance members of any type except object, string, primitives types, and arrays of those.</summary>
        public static PropertiesAndFieldsSelector PublicNonPrimitive = All(false, false);

        public delegate PropertyOrFieldServiceInfo GetInfo(MemberInfo member, Request request);

        /// <summary>Generates selector property and field selector with settings specified by parameters.
        /// If all parameters are omitted the return all public not primitive members.</summary>
        /// <param name="withNonPublic">(optional) Specifies to include non public members. Will include by default.</param>
        /// <param name="withPrimitive">(optional) Specifies to include members of primitive types. Will include by default.</param>
        /// <param name="withFields">(optional) Specifies to include fields as well as properties. Will include by default.</param>
        /// <param name="ifUnresolved">(optional) Defines ifUnresolved behavior for resolved members.</param>
        /// <param name="withInfo">(optional) Return service info for a member or null to skip it resolution.</param>
        /// <returns>Result selector composed using provided settings.</returns>
        public static PropertiesAndFieldsSelector All(
            bool withNonPublic = true, bool withPrimitive = true, bool withFields = true,
            IfUnresolved ifUnresolved = IfUnresolved.ReturnDefault,
            GetInfo withInfo = null)
        {
            GetInfo getInfo = (m, r) => withInfo != null ? withInfo(m, r) :
                  PropertyOrFieldServiceInfo.Of(m).WithDetails(ServiceInfoDetails.Of(ifUnresolved: ifUnresolved), r);
            return r =>
            {
                var properties = r.ImplementationType.GetAll(_ => _.DeclaredProperties)
                    .Where(p => p.Match(withNonPublic, withPrimitive))
                    .Select(m => getInfo(m, r));
                return !withFields ? properties :
                    properties.Concat(r.ImplementationType.GetAll(_ => _.DeclaredFields)
                    .Where(f => f.Match(withNonPublic, withPrimitive))
                    .Select(m => getInfo(m, r)));
            };
        }

        public static PropertiesAndFieldsSelector OverrideWith(this PropertiesAndFieldsSelector s, PropertiesAndFieldsSelector other)
        {
            return s == null || s == Of ? (other ?? Of)
                 : other == null || other == Of ? s
                 : r =>
                {
                    var sourceMembers = s(r).ToArrayOrSelf();
                    var otherMembers = other(r).ToArrayOrSelf();
                    return sourceMembers == null || sourceMembers.Length == 0 ? otherMembers
                        : otherMembers == null || otherMembers.Length == 0 ? sourceMembers
                        : sourceMembers
                            .Where(info => info != null && otherMembers.All(o => o == null || !info.Member.Name.Equals(o.Member.Name)))
                            .Concat(otherMembers);
                };
        }

        public static PropertiesAndFieldsSelector The<T>(this PropertiesAndFieldsSelector s, Expression<Func<T, object>> getterExpression,
            Type requiredServiceType = null, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.ReturnDefault, object defaultValue = null)
        {
            var member = ExpressionTools.GetAccessedMemberOrNull(getterExpression.ThrowIfNull());
            return s.OverrideWith(r => new[] { PropertyOrFieldServiceInfo.Of(member)
                .WithDetails(ServiceInfoDetails.Of(requiredServiceType, serviceKey, ifUnresolved, defaultValue), r) });
        }

        public static PropertiesAndFieldsSelector The<T>(this PropertiesAndFieldsSelector s, Expression<Func<T, object>> getterExpression, Func<IResolver, object> getValue)
        {
            var member = ExpressionTools.GetAccessedMemberOrNull(getterExpression.ThrowIfNull()).ThrowIfNull();
            return s.OverrideWith(r => new[] { PropertyOrFieldServiceInfo.Of(member).WithDetails(ServiceInfoDetails.Of(getValue), r) });
        }

        public static PropertiesAndFieldsSelector The<T>(this PropertiesAndFieldsSelector s, Expression<Func<T, object>> getterExpression, object value)
        {
            return s.The(getterExpression, new Func<IResolver, object>(_ => value));
        }

        public static PropertiesAndFieldsSelector The<T>(this PropertiesAndFieldsSelector s, params Expression<Func<T, object>>[] getterExpressions)
        {
            var infos = getterExpressions.Select(ExpressionTools.GetAccessedMemberOrNull).Select(PropertyOrFieldServiceInfo.Of).ToArray();
            return s.OverrideWith(r => infos);
        }

        public static PropertiesAndFieldsSelector Name(this PropertiesAndFieldsSelector s, string name,
            Type requiredServiceType = null, object serviceKey = null, IfUnresolved ifUnresolved = IfUnresolved.ReturnDefault, object defaultValue = null)
        {
            return s.WithDetails(name, ServiceInfoDetails.Of(requiredServiceType, serviceKey, ifUnresolved, defaultValue));
        }

        public static PropertiesAndFieldsSelector Name(this PropertiesAndFieldsSelector s, string name, object value)
        {
            return s.WithDetails(name, ServiceInfoDetails.Of(_ => value));
        }

        public static PropertiesAndFieldsSelector Name(this PropertiesAndFieldsSelector s, string name, Func<IResolver, object> getValue)
        {
            return s.WithDetails(name, ServiceInfoDetails.Of(getValue));
        }

        #region Tools

        /// <summary>Return either <see cref="PropertyInfo.PropertyType"/>, or <see cref="FieldInfo.FieldType"/> 
        /// depending on actual type of the <paramref name="member"/>.</summary>
        /// <param name="member">Expecting member of type <see cref="PropertyInfo"/> or <see cref="FieldInfo"/> only.</param>
        /// <returns>Type of property of field.</returns>
        public static Type GetPropertyOrFieldType(this MemberInfo member)
        {
            return member is PropertyInfo ? ((PropertyInfo)member).PropertyType : ((FieldInfo)member).FieldType;
        }

        /// <summary>Returns true if property matches flags provided.</summary>
        /// <param name="property">Property to match</param>
        /// <param name="withNonPublic">Says to include non public properties.</param>
        /// <param name="withPrimitive">Says to include properties of primitive type.</param>
        /// <returns>True if property is matched and false otherwise.</returns>
        public static bool Match(this PropertyInfo property, bool withNonPublic = false, bool withPrimitive = false)
        {
            return property.CanWrite && !property.IsIndexer() // first checks that property is assignable in general and not indexer
                && (withNonPublic || property.IsPublic())
                && (withPrimitive || !property.PropertyType.IsPrimitive(orArrayOfPrimitives: true));
        }

        /// <summary>Returns true if field matches flags provided.</summary>
        /// <param name="field">Field to match.</param>
        /// <param name="withNonPublic">Says to include non public fields.</param>
        /// <param name="withPrimitive">Says to include fields of primitive type.</param>
        /// <returns>True if property is matched and false otherwise.</returns>
        public static bool Match(this FieldInfo field, bool withNonPublic = false, bool withPrimitive = false)
        {
            return !field.IsInitOnly && !field.IsBackingField()
                && (withNonPublic || field.IsPublic)
                && (withPrimitive || !field.FieldType.IsPrimitive(orArrayOfPrimitives: true));
        }

        /// <summary>Returns true if field is backing field for property.</summary>
        /// <param name="field">Field to check.</param> <returns>Returns true if field is backing property.</returns>
        public static bool IsBackingField(this FieldInfo field)
        {
            return field.Name[0] == '<';
        }

        /// <summary>Returns true if property is public.</summary>
        /// <param name="property">Property check.</param> <returns>Returns result of check.</returns>
        public static bool IsPublic(this PropertyInfo property)
        {
            return Portable.GetPropertySetMethod(property) != null;
        }

        /// <summary>Returns true if property is indexer: aka this[].</summary>
        /// <param name="property">Property to check</param><returns>True if indexer.</returns>
        public static bool IsIndexer(this PropertyInfo property)
        {
            return property.GetIndexParameters().Length != 0;
        }

        /// <summary>Returns attributes defined for the member/method.</summary>
        /// <param name="member">Member to check.</param> <param name="attributeType">(optional) Specific attribute type to return, any attribute otherwise.</param>
        /// <param name="inherit">Check for inherited member attributes.</param> <returns>Found attributes or empty.</returns>
        public static IEnumerable<Attribute> GetAttributes(this MemberInfo member, Type attributeType = null, bool inherit = false)
        {
            return member.GetCustomAttributes(attributeType ?? typeof(Attribute), inherit).Cast<Attribute>();
        }

        #endregion

        #region Implementation

        private static PropertiesAndFieldsSelector WithDetails(this PropertiesAndFieldsSelector source,
            string name, ServiceInfoDetails details)
        {
            name.ThrowIfNull();
            return source.OverrideWith(r =>
            {
                var implementationType = r.ImplementationType;
                var property = implementationType.GetPropertyOrNull(name);
                if (property != null && property.Match(true, true))
                    return new[] { PropertyOrFieldServiceInfo.Of(property).WithDetails(details, r) };

                var field = implementationType.GetFieldOrNull(name);
                return field != null && field.Match(true, true)
                    ? new[] { PropertyOrFieldServiceInfo.Of(field).WithDetails(details, r) }
                    : Throw.Instead<IEnumerable<PropertyOrFieldServiceInfo>>(Error.NOT_FOUND_SPECIFIED_WRITEABLE_PROPERTY_OR_FIELD, name, r);
            });
        }

        #endregion
    }

    /// <summary>Reflects on <see cref="ImplementationType"/> constructor parameters and members,
    /// creates expression for each reflected dependency, and composes result service expression.</summary>
    public sealed class ReflectionFactory : Factory
    {
        /// <summary>Non-abstract service implementation type. May be open generic.</summary>
        public override Type ImplementationType { get { return _implementationType; } }

        /// <summary>Provides closed-generic factory for registered open-generic variant.</summary>
        public override IConcreteFactoryProvider Provider { get { return _provider; } }

        /// <summary>Injection rules set for Constructor, Parameters, Properties and Fields.</summary>
        public readonly InjectionRules Rules;

        /// <summary>Creates factory providing implementation type, optional reuse and setup.</summary>
        /// <param name="implementationType">Non-abstract close or open generic type.</param>
        /// <param name="reuse">(optional)</param> <param name="rules">(optional)</param> <param name="setup">(optional)</param>
        public ReflectionFactory(Type implementationType, IReuse reuse = null, InjectionRules rules = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _implementationType = implementationType;
            Rules = rules ?? InjectionRules.Default;

            if (Rules.FactoryMethod == null)
                implementationType.ThrowIf(implementationType.IsAbstract(), Error.EXPECTED_NON_ABSTRACT_IMPL_TYPE);

            if (implementationType != null && implementationType.IsGenericDefinition())
                _provider = new CloseGenericFactoryProvider(this);
        }

        /// <summary>Before registering factory checks that ImplementationType is assignable, Or
        /// in case of open generics, compatible with <paramref name="serviceType"/>. 
        /// Then checks that there is defined constructor selector for implementation type with multiple/no constructors.</summary>
        /// <param name="container">Container to register factory in.</param>
        /// <param name="serviceType">Service type to register factory with.</param>
        /// <param name="serviceKey">(ignored)</param>
        public override void BeforeRegistrationCheck(IContainer container, Type serviceType, object serviceKey)
        {
            base.BeforeRegistrationCheck(container, serviceType, serviceKey);
            if (_implementationType == null)
                return;

            var implType = _implementationType;
            if (!implType.IsGenericDefinition())
            {
                if (implType.IsOpenGeneric())
                    Throw.Error(Error.REG_NOT_A_GENERIC_TYPEDEF_IMPL_TYPE,
                        implType, implType.GetGenericDefinitionOrNull());

                if (implType != serviceType && serviceType != typeof(object) &&
                    Array.IndexOf(implType.GetImplementedTypes(), serviceType) == -1)
                    Throw.Error(Error.IMPL_NOT_ASSIGNABLE_TO_SERVICE_TYPE, implType, serviceType);
            }
            else if (implType != serviceType)
            {
                if (serviceType.IsGenericDefinition())
                {
                    var implementedTypes = implType.GetImplementedTypes();
                    var implementedOpenGenericTypes = implementedTypes.Where(t => t.GetGenericDefinitionOrNull() == serviceType);

                    var implTypeArgs = implType.GetGenericParamsAndArgs();
                    Throw.If(!implementedOpenGenericTypes.Any(t => t.ContainsAllGenericParameters(implTypeArgs)),
                        Error.REG_OPEN_GENERIC_SERVICE_WITH_MISSING_TYPE_ARGS,
                        implType, serviceType, implementedOpenGenericTypes);
                }
                else if (implType.IsGeneric() && serviceType.IsOpenGeneric())
                    Throw.Error(Error.REG_NOT_A_GENERIC_TYPEDEF_SERVICE_TYPE,
                        serviceType, serviceType.GetGenericDefinitionOrNull());
                else
                    Throw.Error(Error.REG_OPEN_GENERIC_IMPL_WITH_NON_GENERIC_SERVICE, implType, serviceType);
            }

            if (container.Rules.FactoryMethod == null && Rules.FactoryMethod == null)
            {
                var publicCtorCount = implType.GetAllConstructors().Count();
                Throw.If(publicCtorCount != 1, Error.NO_CTOR_SELECTOR_FOR_IMPL_WITH_MULTIPLE_CTORS, implType, publicCtorCount);
            }
        }

        /// <summary>Creates service expression, so for registered implementation type "Service", 
        /// you will get "new Service()". If there is <see cref="Reuse"/> specified, then expression will
        /// contain call to <see cref="Scope"/> returned by reuse.</summary>
        /// <param name="request">Request for service to resolve.</param> <returns>Created expression.</returns>
        public override Expression CreateExpressionOrDefault(Request request)
        {
            var method = GetFactoryMethodOrDefault(request);
            if (method == null)
                return null;

            var parameters = method.Method.GetParameters();

            Expression[] paramExprs = null;
            if (parameters.Length != 0)
            {
                paramExprs = new Expression[parameters.Length];

                var getParamInfo = request.Container.Rules.Parameters.OverrideWith(Rules.Parameters);

                var funcArgs = request.FuncArgs;
                var funcArgsUsedMask = 0;

                for (var i = 0; i < parameters.Length; i++)
                {
                    var ctorParam = parameters[i];
                    Expression paramExpr = null;

                    if (funcArgs != null)
                    {
                        for (var fa = 0; fa < funcArgs.Value.Length && paramExpr == null; ++fa)
                        {
                            var funcArg = funcArgs.Value[fa];
                            if ((funcArgsUsedMask & 1 << fa) == 0 &&                  // not yet used func argument
                                funcArg.Type.IsAssignableTo(ctorParam.ParameterType)) // and it assignable to parameter
                            {
                                paramExpr = funcArg;
                                funcArgsUsedMask |= 1 << fa;  // mark that argument was used
                                funcArgs.Key[fa] = true;      // mark that argument was used globally for Func<..> resolver.
                            }
                        }
                    }

                    // If parameter expression still null (no Func argument to substitute), try to resolve it
                    if (paramExpr == null)
                    {
                        var paramInfo = getParamInfo(ctorParam, request) ?? ParameterServiceInfo.Of(ctorParam);
                        var paramRequest = request.Push(paramInfo);

                        var factory = paramInfo.Details.GetValue == null
                            ? paramRequest.Container.ResolveFactory(paramRequest)
                            : new DelegateFactory(r => paramRequest.ServiceInfo.Details.GetValue(r)
                                .ThrowIfNotOf(paramRequest.ServiceType, Error.INJECTED_VALUE_IS_OF_DIFFERENT_TYPE, paramRequest));

                        paramExpr = factory == null ? null : factory.GetExpressionOrDefault(paramRequest);
                        if (paramExpr == null)
                        {
                            if (request.IfUnresolved == IfUnresolved.ReturnDefault)
                                return null;

                            var defaultValue = paramInfo.Details.DefaultValue;
                            paramExpr = defaultValue != null
                                ? paramRequest.StateCache.GetOrAddItemExpression(defaultValue)
                                : paramRequest.ServiceType.GetDefaultValueExpression();
                        }
                    }

                    paramExprs[i] = paramExpr;
                }
            }

            return MakeServiceExpression(method, request, paramExprs);
        }

        #region Implementation

        private readonly Type _implementationType;
        private readonly CloseGenericFactoryProvider _provider;

        private sealed class CloseGenericFactoryProvider : IConcreteFactoryProvider
        {
            public IEnumerable<KV<Type, object>> ProvidedFactoriesServiceTypeKey
            {
                get { return _providedFactories.Value.IsEmpty
                    ? Enumerable.Empty<KV<Type, object>>()
                    : _providedFactories.Value.Enumerate().Select(_ => _.Value); }
            }

            public CloseGenericFactoryProvider(ReflectionFactory factory) { _factory = factory; }

            public Factory ProvideConcreteFactory(Request request)
            {
                var serviceType = request.ServiceType;
                var implType = _factory._implementationType;
                var closedTypeArgs = implType == serviceType.GetGenericDefinitionOrNull()
                    ? serviceType.GetGenericParamsAndArgs()
                    : GetClosedTypeArgsForGenericImplementationTypeOrNull(implType, request);
                if (closedTypeArgs == null)
                    return null;

                Type closedImplType;
                if (request.IfUnresolved == IfUnresolved.ReturnDefault)
                {
                    try { closedImplType = implType.MakeGenericType(closedTypeArgs); }
                    catch { return null; }
                }
                else
                {
                    closedImplType = Throw.IfThrows<ArgumentException, Type>(
                       () => implType.MakeGenericType(closedTypeArgs),
                       Error.NOT_MATCHED_GENERIC_PARAM_CONSTRAINTS, implType, request);
                }

                var factory = new ReflectionFactory(closedImplType, _factory.Reuse, _factory.Rules, _factory.Setup);
                _providedFactories.Swap(_ => _.AddOrUpdate(
                    key: factory.FactoryID,
                    value: new KV<Type, object>(serviceType, request.ServiceKey)));
                return factory;

            }

            private readonly ReflectionFactory _factory;

            private readonly Ref<HashTree<int, KV<Type, object>>>
                _providedFactories = Ref.Of(HashTree<int, KV<Type, object>>.Empty);
        }

        private FactoryMethod GetFactoryMethodOrDefault(Request request)
        {
            var implType = _implementationType;
            var getMethodOrNull = Rules.FactoryMethod ?? request.Container.Rules.FactoryMethod;
            if (getMethodOrNull != null)
            {
                var method = getMethodOrNull(request);
                if (method != null && method.Method is MethodInfo)
                {
                    Throw.If(method.Method.IsStatic && method.Factory != null,
                        Error.FACTORY_OBJ_PROVIDED_BUT_METHOD_IS_STATIC, method.Factory, method, request);

                    request.ServiceType.ThrowIfNotOf(((MethodInfo)method.Method).ReturnType,
                        Error.SERVICE_IS_NOT_ASSIGNABLE_FROM_FACTORY_METHOD, method, request);

                    if (!method.Method.IsStatic && method.Factory == null)
                        return request.IfUnresolved == IfUnresolved.ReturnDefault ? null
                            : Throw.Instead<FactoryMethod>(Error.FACTORY_OBJ_IS_NULL_IN_FACTORY_METHOD, method, request);
                }

                return method.ThrowIfNull(Error.UNABLE_TO_SELECT_CTOR_USING_SELECTOR, implType);
            }

            var ctors = implType.GetAllConstructors().ToArrayOrSelf();
            Throw.If(ctors.Length == 0, Error.NO_PUBLIC_CONSTRUCTOR_DEFINED, implType);
            Throw.If(ctors.Length > 1, Error.UNABLE_TO_SELECT_CTOR, ctors.Length, implType);
            return ctors[0];
        }

        private Expression SetPropertiesAndFields(NewExpression newServiceExpr, Request request)
        {
            var getMemberInfos = request.Container.Rules.PropertiesAndFields.OverrideWith(Rules.PropertiesAndFields);
            var memberInfos = getMemberInfos(request);
            if (memberInfos == null)
                return newServiceExpr;

            var bindings = new List<MemberBinding>();
            foreach (var memberInfo in memberInfos)
                if (memberInfo != null)
                {
                    var memberRequest = request.Push(memberInfo);
                    var factory = memberInfo.Details.GetValue == null
                        ? memberRequest.Container.ResolveFactory(memberRequest)
                        : new DelegateFactory(r => memberRequest.ServiceInfo.Details.GetValue(r)
                            .ThrowIfNotOf(memberRequest.ServiceType, Error.INJECTED_VALUE_IS_OF_DIFFERENT_TYPE, memberRequest));

                    var memberExpr = factory == null ? null : factory.GetExpressionOrDefault(memberRequest);
                    if (memberExpr == null && request.IfUnresolved == IfUnresolved.ReturnDefault)
                        return null;
                    if (memberExpr != null)
                        bindings.Add(Expression.Bind(memberInfo.Member, memberExpr));
                }

            return bindings.Count == 0 ? (Expression)newServiceExpr : Expression.MemberInit(newServiceExpr, bindings);
        }

        private Expression MakeServiceExpression(FactoryMethod method, Request request, Expression[] paramExprs)
        {
            return method.Method.IsConstructor
                ? SetPropertiesAndFields(Expression.New((ConstructorInfo)method.Method, paramExprs), request)
                : method.Method.IsStatic
                    ? Expression.Call((MethodInfo)method.Method, paramExprs)
                    : Expression.Call(request.StateCache.GetOrAddItemExpression(method.Factory), (MethodInfo)method.Method, paramExprs);
        }

        private static Type[] GetClosedTypeArgsForGenericImplementationTypeOrNull(Type implType, Request request)
        {
            var serviceType = request.ServiceType;
            var serviceTypeArgs = serviceType.GetGenericParamsAndArgs();
            var serviceTypeGenericDef = serviceType.GetGenericDefinitionOrNull().ThrowIfNull();

            var openImplTypeParams = implType.GetGenericParamsAndArgs();
            var implementedTypes = implType.GetImplementedTypes();

            Type[] resultImplTypeArgs = null;
            for (var i = 0; resultImplTypeArgs == null && i < implementedTypes.Length; i++)
            {
                var implementedType = implementedTypes[i];
                if (implementedType.IsOpenGeneric() &&
                    implementedType.GetGenericDefinitionOrNull() == serviceTypeGenericDef)
                {
                    var matchedTypeArgs = new Type[openImplTypeParams.Length];
                    if (MatchServiceWithImplementedTypeArgs(ref matchedTypeArgs,
                        openImplTypeParams, implementedType.GetGenericParamsAndArgs(), serviceTypeArgs))
                        resultImplTypeArgs = matchedTypeArgs;
                }
            }

            if (resultImplTypeArgs == null)
                return request.IfUnresolved == IfUnresolved.ReturnDefault ? null :
                    Throw.Instead<Type[]>(Error.NOT_MATCHED_IMPL_BASE_TYPES_WITH_SERVICE_TYPE,
                        implType, implementedTypes, request);

            var unmatchedArgIndex = Array.IndexOf(resultImplTypeArgs, null);
            if (unmatchedArgIndex != -1)
                return request.IfUnresolved == IfUnresolved.ReturnDefault ? null :
                    Throw.Instead<Type[]>(Error.NOT_FOUND_OPEN_GENERIC_IMPL_TYPE_ARG_IN_SERVICE,
                        implType, openImplTypeParams[unmatchedArgIndex], request);

            return resultImplTypeArgs;
        }

        private static bool MatchServiceWithImplementedTypeArgs(ref Type[] matchedServiceArgs,
            Type[] openImplementationParams, Type[] openImplementedParams, Type[] closedServiceArgs)
        {
            for (var i = 0; i < openImplementedParams.Length; i++)
            {
                var openImplementedParam = openImplementedParams[i];
                var closedServiceArg = closedServiceArgs[i];
                if (openImplementedParam.IsGenericParameter)
                {
                    var matchedIndex = openImplementationParams.IndexOf(t => t.Name == openImplementedParam.Name);
                    if (matchedIndex != -1)
                    {
                        if (matchedServiceArgs[matchedIndex] == null)
                            matchedServiceArgs[matchedIndex] = closedServiceArg;
                        else if (matchedServiceArgs[matchedIndex] != closedServiceArg)
                            return false; // more than one closedServiceArg is matching with single openArg
                    }
                }
                else if (openImplementedParam != closedServiceArg)
                {
                    if (!openImplementedParam.IsOpenGeneric() ||
                        openImplementedParam.GetGenericDefinitionOrNull() != closedServiceArg.GetGenericDefinitionOrNull())
                        return false; // openArg and closedArg are different types

                    if (!MatchServiceWithImplementedTypeArgs(ref matchedServiceArgs, openImplementationParams,
                        openImplementedParam.GetGenericParamsAndArgs(), closedServiceArg.GetGenericParamsAndArgs()))
                        return false; // nested match failed due either one of above reasons.
                }
            }

            return true;
        }

        #endregion
    }

    /// <summary>Creates service expression using client provided expression factory delegate.</summary>
    public sealed class ExpressionFactory : Factory
    {
        /// <summary>Wraps provided delegate into factory.</summary>
        /// <param name="getServiceExpression">Delegate that will be used internally to create service expression.</param>
        /// <param name="reuse">(optional) Reuse.</param> <param name="setup">(optional) Setup.</param>
        public ExpressionFactory(Func<Request, Expression> getServiceExpression, IReuse reuse = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _getServiceExpression = getServiceExpression.ThrowIfNull();
        }

        /// <summary>Creates service expression using wrapped delegate.</summary>
        /// <param name="request">Request to resolve.</param> <returns>Expression returned by stored delegate.</returns>
        public override Expression CreateExpressionOrDefault(Request request)
        {
            return _getServiceExpression(request);
        }

        private readonly Func<Request, Expression> _getServiceExpression;
    }

    /// <summary>This factory is the thin wrapper for user provided delegate 
    /// and where possible it uses delegate directly: without converting it to expression.</summary>
    public sealed class DelegateFactory : Factory
    {
        /// <summary>Creates factory by providing:</summary>
        /// <param name="factoryDelegate">User specified service creation delegate.</param>
        /// <param name="reuse">Reuse behavior for created service.</param>
        /// <param name="setup">Additional settings.</param>
        public DelegateFactory(Func<IResolver, object> factoryDelegate, IReuse reuse = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _factoryDelegate = factoryDelegate.ThrowIfNull();
        }

        /// <summary>Create expression by wrapping call to stored delegate with provided request.</summary>
        /// <param name="request">Request to resolve. It will be stored in resolution state to be passed to delegate on actual resolve.</param>
        /// <returns>Created delegate call expression.</returns>
        public override Expression CreateExpressionOrDefault(Request request)
        {
            var factoryDelegateExpr = request.StateCache.GetOrAddItemExpression(_factoryDelegate);
            return Expression.Convert(Expression.Invoke(factoryDelegateExpr, Container.ResolverExpr), request.ServiceType);
        }

        /// <summary>If possible returns delegate directly, without creating expression trees, just wrapped in <see cref="FactoryDelegate"/>.
        /// If decorator found for request then factory fall-backs to expression creation.</summary>
        /// <param name="request">Request to resolve.</param> 
        /// <returns>Factory delegate directly calling wrapped delegate, or invoking expression if decorated.</returns>
        public override FactoryDelegate GetDelegateOrDefault(Request request)
        {
            request = request.ResolveWithFactory(this);

            if (request.Container.GetDecoratorExpressionOrDefault(request) != null)
                return base.GetDelegateOrDefault(request);

            var rules = request.Container.Rules;
            var reuse = rules.ReuseMapping == null ? Reuse : rules.ReuseMapping(Reuse, request);
            ThrowIfReuseHasShorterLifespanThanParent(reuse, request);

            if (reuse == null)
                return (state, r, scope) => _factoryDelegate(r.Resolver);

            var reuseIndex = request.StateCache.GetOrAddItem(reuse);
            return (state, r, scope) => ((IReuse)state.Get(reuseIndex))
                .GetScope(r.Resolver, ref scope)
                .GetOrAdd(FactoryID, () => _factoryDelegate(r.Resolver));
        }

        private readonly Func<IResolver, object> _factoryDelegate;
    }

    /// <summary>Lazy object storage that will create object with provided factory on first access, 
    /// then will be returning the same object for subsequent access.</summary>
    public interface IScope : IDisposable
    {
        /// <summary>Parent scope in scope stack. Null for root scope.</summary>
        IScope Parent { get; }

        /// <summary>Optional name object associated with scope.</summary>
        object Name { get; }

        /// <summary>Creates, stores, and returns stored object.</summary>
        /// <param name="id">Unique ID to find created object in subsequent calls.</param>
        /// <param name="factory">Delegate to create object. It will be used immediately, and reference to delegate will Not be stored.</param>
        /// <returns>Created and stored object.</returns>
        object GetOrAdd(int id, Func<object> factory);
    }

    /// <summary><see cref="IScope"/> implementation which will dispose stored <see cref="IDisposable"/> items on its own dispose.
    /// Locking is used internally to ensure that object factory called only once.</summary>
    public sealed class Scope : IScope
    {
        /// <summary>Parent scope in scope stack. Null for root scope.</summary>
        public IScope Parent { get; private set; }

        /// <summary>Optional name object associated with scope.</summary>
        public object Name { get; private set; }

        /// <summary>Returns true if scope disposed.</summary>
        public bool IsDisposed
        {
            get { return _disposed == 1; }
        }

        /// <summary>Accumulates exceptions thrown by disposed items.</summary>
        public Exception[] DisposingExceptions;

        /// <summary>Create scope with optional parent and name.</summary>
        /// <param name="parent">(optional) Parent in scope stack.</param>
        /// <param name="name">(optional) Associated name object, e.g. <see cref="IScopeContext.RootScopeName"/></param>
        public Scope(IScope parent = null, object name = null)
        {
            Parent = parent;
            Name = name;
        }

        /// <summary>Provides access to <see cref="GetOrAdd"/> method for reflection client.</summary>
        public static readonly MethodInfo GetOrAddMethod = typeof(IScope).GetSingleDeclaredMethodOrNull("GetOrAdd");

        /// <summary><see cref="IScope.GetOrAdd"/> for description.
        /// Will throw <see cref="ContainerException"/> if scope is disposed.</summary>
        /// <param name="id">Unique ID to find created object in subsequent calls.</param>
        /// <param name="factory">Delegate to create object. It will be used immediately, and reference to delegate will Not be stored.</param>
        /// <returns>Created and stored object.</returns>
        /// <exception cref="ContainerException">if scope is disposed.</exception>
        public object GetOrAdd(int id, Func<object> factory)
        {
            if (_disposed == 1)
                Throw.Error(Error.SCOPE_IS_DISPOSED);

            lock (_syncRoot)
            {
                var item = _items.GetValueOrDefault(id);
                if (item == null ||
                    item is IRecyclable && ((IRecyclable)item).IsRecycled)
                {
                    if (item != null)
                        DisposeItem(item);

                    Ref.Swap(ref _items, items => _disposed == 1
                        ? Throw.Instead<IntKeyTree>(Error.SCOPE_IS_DISPOSED)
                        : items.AddOrUpdate(id, item = factory()));
                }
                return item;
            }
        }

        /// <summary>Disposes all stored <see cref="IDisposable"/> objects and nullifies object storage.</summary>
        /// <remarks>If item disposal throws exception, then it won't be propagated outside, so the rest of the items could be disposed.
        /// Rather all thrown exceptions are aggregated in <see cref="DisposingExceptions"/> array. If no exceptions, array is null.</remarks>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            if (!_items.IsEmpty)
                foreach (var item in _items.Enumerate().Select(x => x.Value).Where(x => x is IDisposable || x is IReuseWrapper))
                    DisposeItem(item);
            _items = null;
        }

        #region Implementation

        private IntKeyTree _items = IntKeyTree.Empty;
        private int _disposed;

        // Sync root is required to create object only once. The same reason as for Lazy<T>.
        private readonly object _syncRoot = new object();

        private void DisposeItem(object item)
        {
            try
            {
                var disposable = item as IDisposable;
                if (disposable != null)
                    disposable.Dispose();
                else
                {
                    var reused = item as IReuseWrapper;
                    while (reused != null && !(reused is IHideDisposableFromContainer)
                           && reused.Target != null && (disposable = reused.Target as IDisposable) == null)
                        reused = reused.Target as IReuseWrapper;
                    if (disposable != null)
                        disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                DisposingExceptions.AppendOrUpdate(ex);
            }
        }

        #endregion
    }

    /// <summary>Provides ambient current scope and optionally scope storage for container, 
    /// examples are HttpContext storage, Execution context, Thread local.</summary>
    public interface IScopeContext
    {
        /// <summary>Allow to associate some name with context, so it could be used by reuse to find what context 
        /// is used by container.</summary>
        object RootScopeName { get; }

        /// <summary>Returns current scope or null if no ambient scope available at the moment.</summary>
        /// <returns>Current scope or null.</returns>
        IScope GetCurrentOrDefault();

        /// <summary>Changes current scope using provided delegate. Delegate receives current scope as input and
        /// should return new current scope.</summary>
        /// <param name="getNewCurrentScope">Delegate to change the scope.</param>
        /// <remarks>Important: <paramref name="getNewCurrentScope"/> may be called multiple times in concurrent environment.
        /// Make it predictable by removing any side effects.</remarks>
        /// <returns>New current scope. So it is convenient to use method in "using (var newScope = ctx.SetCurrent(...))".</returns>
        IScope SetCurrent(Func<IScope, IScope> getNewCurrentScope);
    }

    /// <summary>Tracks one current scope per thread, so the current scope in different tread would be different or null,
    /// if not yet tracked. Context actually stores scope references internally, so it should be disposed to free them.</summary>
    public sealed class ThreadScopeContext : IScopeContext, IDisposable
    {
        /// <summary>Provides static access to <see cref="RootScopeName"/>. It is OK because its constant.</summary>
        public static readonly object ROOT_SCOPE_NAME = typeof(ThreadScopeContext);

        /// <summary>Key to identify context.</summary>
        public object RootScopeName { get { return ROOT_SCOPE_NAME; } }

        /// <summary>Returns current scope in calling Thread or null, if no scope tracked.</summary>
        /// <returns>Found scope or null.</returns>
        public IScope GetCurrentOrDefault()
        {
            return _scopes.GetValueOrDefault(Portable.GetCurrentManagedThreadID()) as IScope;
        }

        /// <summary>Change current scope for the calling Thread.</summary>
        /// <param name="getNewCurrentScope">Delegate to change the scope given current one (or null).</param>
        /// <remarks>Important: <paramref name="getNewCurrentScope"/> may be called multiple times in concurrent environment.
        /// Make it predictable by removing any side effects.</remarks>
        public IScope SetCurrent(Func<IScope, IScope> getNewCurrentScope)
        {
            var threadId = Portable.GetCurrentManagedThreadID();
            IScope newScope = null;
            Ref.Swap(ref _scopes, scopes =>
                scopes.AddOrUpdate(threadId, newScope = getNewCurrentScope(scopes.GetValueOrDefault(threadId) as IScope)));
            return newScope;
        }

        /// <summary>Disposed all stored/tracked scopes and empties internal scope storage.</summary>
        public void Dispose()
        {
            Ref.Swap(ref _scopes, scopes =>
            {
                if (!scopes.IsEmpty)
                    foreach (var scope in scopes.Enumerate().Where(scope => scope.Value is IDisposable))
                        ((IDisposable)scope.Value).Dispose();
                return IntKeyTree.Empty;
            });
        }

        private IntKeyTree _scopes = IntKeyTree.Empty;
    }

    /// <summary>Reuse goal is to locate or create scope where reused objects will be stored.</summary>
    /// <remarks><see cref="IReuse"/> implementors supposed to be stateless, and provide scope location behavior only.
    /// The reused service instances should be stored in scope(s).</remarks>
    public interface IReuse
    {
        /// <summary>Relative to other reuses lifespan value.</summary>
        int Lifespan { get; }

        /// <summary>Locates or creates scope to store reused service objects.</summary>
        /// <returns>Located scope.</returns>
        IScope GetScope(IResolverWithScopes resolverWithScopes, ref IScope resolutionScope);

        /// <summary>Supposed to create in-line expression with the same code as body of <see cref="GetScope"/> method.</summary>
        /// <param name="resolverWithScopesExpr"><see cref="IResolverWithScopes"/> to provide access to resolver and scopes.</param>
        /// <param name="resolutionScopeExpr">Expression represent current value of resolution scope, initially it is null.</param>
        /// <param name="request">Request to get context information or for example store something in resolution state.</param>
        /// <returns>Expression of type <see cref="IScope"/>.</returns>
        /// <remarks>Result expression should be static: should Not create closure on any objects. 
        /// If you require to reference some item from outside, put it into <see cref="Request.StateCache"/>.</remarks>
        Expression GetScopeExpression(Expression resolverWithScopesExpr, Expression resolutionScopeExpr, Request request);
    }

    /// <summary>Returns container bound scope for storing singleton objects.</summary>
    public sealed class SingletonReuse : IReuse
    {
        /// <summary>Related lifespan value associated with reuse.</summary>
        public static readonly int LIFESPAN = 1000;

        /// <summary>Relative to other reuses lifespan value.</summary>
        public int Lifespan { get { return LIFESPAN; } }

        /// <summary>Returns container bound Singleton scope.</summary>
        /// <param name="resolverWithScopes">Provides access to Resolver and Scopes.</param>
        /// <param name="resolutionScope">Scope associated with resolution root.</param>
        /// <returns>Container singleton scope.</returns>
        public IScope GetScope(IResolverWithScopes resolverWithScopes, ref IScope resolutionScope)
        {
            return resolverWithScopes.SingletonScope;
        }

        /// <summary>Returns expression directly accessing <see cref="IResolverWithScopes.SingletonScope"/>.</summary>
        /// <param name="resolverWithScopesExpr"><see cref="IResolverWithScopes"/> expression.</param>
        /// <param name="_">(ignored)</param> <param name="__">(ignored)</param>
        /// <returns>Singleton scope property expression.</returns>
        public Expression GetScopeExpression(Expression resolverWithScopesExpr, Expression _, Request __)
        {
            return Expression.Property(resolverWithScopesExpr, "SingletonScope");
        }

        /// <summary>Pretty print reuse name and lifespan</summary> <returns>Printed string.</returns>
        public override string ToString() { return GetType().Name + ":" + Lifespan; }
    }

    /// <summary>Returns container bound current scope created by <see cref="Container.OpenScope"/> method.</summary>
    /// <remarks>It is the same as Singleton scope if container was not created by <see cref="Container.OpenScope"/>.</remarks>
    public sealed class CurrentScopeReuse : IReuse
    {
        /// <summary>Related lifespan value associated with reuse.</summary>
        public static readonly int LIFESPAN = 100;

        /// <summary>Name to find current scope or parent with equal name.</summary>
        public readonly object Name;

        /// <summary>Relative to other reuses lifespan value.</summary>
        public int Lifespan { get { return LIFESPAN; } }

        /// <summary>Creates reuse optionally specifying its name.</summary> 
        /// <param name="name">(optional) Used to find matching current scope or parent.</param>
        public CurrentScopeReuse(object name = null)
        {
            Name = name;
        }

        /// <summary>Returns container current scope or if <see cref="Name"/> specified: current scope or its parent with corresponding name.</summary>
        /// <param name="resolverWithScopes">Provides access to Resolver and Scopes, e.g. CurrentScope.</param>
        /// <param name="resolutionScope">Scope associated with resolution root.</param>
        /// <returns>Found current scope or its parent.</returns>
        /// <exception cref="ContainerException">with the code <see cref="Error.NO_MATCHED_SCOPE_FOUND"/> if <see cref="Name"/> specified but
        /// no matching scope or its parent found.</exception>
        public IScope GetScope(IResolverWithScopes resolverWithScopes, ref IScope resolutionScope)
        {
            return GetNameMatchingScope(resolverWithScopes.CurrentScope, Name);
        }

        /// <summary>Returns <see cref="GetNameMatchingScope"/> method call expression, which returns current scope,
        /// or current scope parent matched by name, or throws exception if no current scope is available.</summary>
        /// <param name="resolverWithScopesExpr">Access to <see cref="IResolverWithScopes"/>.</param>
        /// <param name="_">(ignored)</param>
        /// <param name="request">Used to access resolution state to store <see cref="Name"/> in (if its non-primitive).</param>
        /// <returns>Method call expression returning matched current scope.</returns>
        public Expression GetScopeExpression(Expression resolverWithScopesExpr, Expression _, Request request)
        {
            return Expression.Call(typeof(CurrentScopeReuse), "GetNameMatchingScope", null,
                Expression.Property(resolverWithScopesExpr, "CurrentScope"),
                request.StateCache.GetOrAddItemExpression(Name, typeof(object)));
        }

        /// <summary>Returns current scope, or current scope parent matched by name, 
        /// or throws exception if no current scope is available.</summary>
        /// <param name="scope">Current scope to start match from.</param>
        /// <param name="nameToMatch">Name to match with current scopes stack names.</param>
        /// <returns>Current scope or its ancestor.</returns>
        /// <exception cref="ContainerException"> with code <see cref="Error.NO_MATCHED_SCOPE_FOUND"/>.</exception>
        public static IScope GetNameMatchingScope(IScope scope, object nameToMatch)
        {
            if (nameToMatch == null)
                return scope;
            while (scope != null && !nameToMatch.Equals(scope.Name))
                scope = scope.Parent;
            return scope.ThrowIfNull(Error.NO_MATCHED_SCOPE_FOUND, nameToMatch);
        }

        /// <summary>Pretty prints reuse to string.</summary> <returns>Reuse string.</returns>
        public override string ToString()
        {
            var s = GetType().Name + ":" + Lifespan;
            if (Name != null)
                s += new StringBuilder(", Name:").Print(Name);
            return s;
        }
    }

    /// <summary>Represents services created once per resolution root (when some of Resolve methods called).</summary>
    /// <remarks>Scope is created only if accessed to not waste memory.</remarks>
    public sealed class ResolutionScopeReuse : IReuse
    {
        /// <summary>Related lifespan value associated with reuse.</summary>
        public static readonly int LIFESPAN = 10;

        /// <summary>Relative to other reuses lifespan value.</summary>
        public int Lifespan { get { return LIFESPAN; } }

        /// <summary>Creates or returns already created resolution root scope.</summary>
        /// <param name="_">(ignored)</param>
        /// <param name="resolutionScope">Scope associated with resolution root.</param>
        /// <returns>Created or existing scope.</returns>
        public IScope GetScope(IResolverWithScopes _, ref IScope resolutionScope)
        {
            return GetOrCreateScope(ref resolutionScope);
        }

        /// <summary>Returns <see cref="GetOrCreateScope"/> method call expression applied to <paramref name="resolutionScopeExpr"/>.</summary>
        /// <param name="_">(ignored)</param> 
        /// <param name="resolutionScopeExpr">Resolution scope expression.</param>
        /// <param name="__">(ignored).</param>
        /// <returns>Method call expression returning existing or newly created resolution scope.</returns>
        public Expression GetScopeExpression(Expression _, Expression resolutionScopeExpr, Request __)
        {
            return Expression.Call(typeof(ResolutionScopeReuse), "GetOrCreateScope", null, resolutionScopeExpr);
        }

        /// <summary>Check if referenced input scope is not null, then just returns it, otherwise creates it,
        /// sets <paramref name="resolutionScope"/> and returns it.</summary>
        /// <param name="resolutionScope">May be null scope.</param>
        /// <returns>Input <paramref name="resolutionScope"/> ensuring it is not null.</returns>
        public static IScope GetOrCreateScope(ref IScope resolutionScope)
        {
            return resolutionScope ?? (resolutionScope = new Scope());
        }

        /// <summary>Pretty print reuse name and lifespan</summary> <returns>Printed string.</returns>
        public override string ToString() { return GetType().Name + ":" + Lifespan; }
    }

    /// <summary>Specifies pre-defined reuse behaviors supported by container: 
    /// used when registering services into container with <see cref="Registrator"/> methods.</summary>
    public static partial class Reuse
    {
        /// <summary>Synonym for absence of reuse.</summary>
        public static readonly IReuse Transient = null; // no reuse.

        /// <summary>Specifies to store single service instance per <see cref="Container"/>.</summary>
        public static readonly IReuse Singleton = new SingletonReuse();

        /// <summary>Specifies to store single service instance per resolution root created by <see cref="Resolver"/> methods.</summary>
        public static readonly IReuse InResolutionScope = new ResolutionScopeReuse();

        /// <summary>Specifies to store single service instance per current/open scope created with <see cref="Container.OpenScope"/>.</summary>
        public static readonly IReuse InCurrentScope = new CurrentScopeReuse();

        /// <summary>Return current scope reuse with specific name to match with scope.
        /// If name is not specified then function returns <see cref="InCurrentScope"/>.</summary>
        /// <param name="name">(optional) Name to match with scope.</param>
        /// <returns>Created current scope reuse.</returns>
        public static IReuse InCurrentNamedScope(object name = null)
        {
            return name == null ? InCurrentScope : new CurrentScopeReuse(name);
        }

        /// <summary>Ensuring single service instance per Thread.</summary>
        public static readonly IReuse InThreadScope = InCurrentNamedScope(ThreadScopeContext.ROOT_SCOPE_NAME);
    }

    /// <summary>Creates <see cref="IReuseWrapper"/> for target and unwraps matching wrapper.</summary>
    public interface IReuseWrapperFactory
    {
        /// <summary>Wraps target value into new wrapper.</summary>
        /// <param name="target">Input value. May be other wrapper.</param> <returns>New wrapper.</returns>
        object Wrap(object target);

        /// <summary>Unwraps wrapper of supported/matched wrapper type. Otherwise throws.</summary>
        /// <param name="wrapper">Wrapper to unwrap.</param> <returns>Unwrapped value. May be nested wrapper.</returns>
        object Unwrap(object wrapper);
    }

    /// <summary>Listing and implementations of out-of-the-box supported <see cref="IReuseWrapper"/> factories.</summary>
    public static class ReuseWrapperFactory
    {
        /// <summary>Factory for <see cref="ReuseHiddenDisposable"/>.</summary>
        public static readonly IReuseWrapperFactory HiddenDisposable = new HiddenDisposableFactory();

        /// <summary>Factory for <see cref="ReuseWeakReference"/>.</summary>
        public static readonly IReuseWrapperFactory WeakReference = new WeakReferenceFactory();

        /// <summary>Factory for <see cref="ReuseSwapable"/>.</summary>
        public static readonly IReuseWrapperFactory Swapable = new SwapableFactory();

        /// <summary>Factory for <see cref="ReuseRecyclable"/>.</summary>
        public static readonly IReuseWrapperFactory Recyclable = new RecyclableFactory();

        #region Implementation

        private sealed class HiddenDisposableFactory : IReuseWrapperFactory
        {
            public object Wrap(object target)
            {
                return new ReuseHiddenDisposable((target as IDisposable).ThrowIfNull());
            }

            public object Unwrap(object wrapper)
            {
                return (wrapper as ReuseHiddenDisposable).ThrowIfNull().Target;
            }
        }

        private sealed class WeakReferenceFactory : IReuseWrapperFactory
        {
            public object Wrap(object target)
            {
                return new ReuseWeakReference(target);
            }

            public object Unwrap(object wrapper)
            {
                return (wrapper as ReuseWeakReference).ThrowIfNull().Target.ThrowIfNull(Error.WEAKREF_REUSE_WRAPPER_GCED);
            }
        }

        private sealed class SwapableFactory : IReuseWrapperFactory
        {
            public object Wrap(object target)
            {
                return new ReuseSwapable(target);
            }

            public object Unwrap(object wrapper)
            {
                return (wrapper as ReuseSwapable).ThrowIfNull().Target;
            }
        }

        private sealed class RecyclableFactory : IReuseWrapperFactory
        {
            public object Wrap(object target)
            {
                return new ReuseRecyclable(target);
            }

            public object Unwrap(object wrapper)
            {
                var recyclable = (wrapper as ReuseRecyclable).ThrowIfNull();
                Throw.If(recyclable.IsRecycled, Error.RECYCLABLE_REUSE_WRAPPER_IS_RECYCLED);
                return recyclable.Target;
            }
        }

        #endregion
    }

    /// <summary>Defines reused object wrapper.</summary>
    public interface IReuseWrapper
    {
        /// <summary>Wrapped value.</summary>
        object Target { get; }
    }

    /// <summary>Provides strongly-typed access to wrapped target.</summary>
    public static class ReuseWrapper
    {
        /// <summary>Unwraps input until target of <typeparamref name="T"/> is found. Returns found target, otherwise returns null.</summary>
        /// <typeparam name="T">Target to stop search on.</typeparam>
        /// <param name="reuseWrapper">Source reused wrapper to get target from.</param>
        public static T TargetOrDefault<T>(this IReuseWrapper reuseWrapper) where T : class
        {
            var target = reuseWrapper.ThrowIfNull().Target;
            while (!(target is T) && (target is IReuseWrapper))
                target = ((IReuseWrapper)target).Target;
            return target as T;
        }
    }

    /// <summary>Marker interface used by Scope to skip dispose for reused disposable object.</summary>
    public interface IHideDisposableFromContainer { }

    /// <summary>Wraps reused service object to prevent container to dispose service object. Intended to work only with <see cref="IDisposable"/> target.</summary>
    public class ReuseHiddenDisposable : IReuseWrapper, IHideDisposableFromContainer
    {
        /// <summary>Constructs wrapper by wrapping input target.</summary>
        /// <param name="target">Disposable target.</param>
        public ReuseHiddenDisposable(IDisposable target)
        {
            _target = target;
            _targetType = target.GetType();
        }

        /// <summary>Wrapped value.</summary>
        public object Target
        {
            get
            {
                Throw.If(IsDisposed, Error.TARGET_WAS_ALREADY_DISPOSED, _targetType, typeof(ReuseHiddenDisposable));
                return _target;
            }
        }

        /// <summary>True if target was disposed.</summary>
        public bool IsDisposed
        {
            get { return _disposed == 1; }
        }

        /// <summary>Dispose target and mark wrapper as disposed.</summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;
            _target.Dispose();
            _target = null;
        }

        #region Implementation

        private int _disposed;
        private IDisposable _target;
        private readonly Type _targetType;

        #endregion
    }

    /// <summary>Wraps reused object as <see cref="WeakReference"/>. Allow wrapped object to be garbage collected.</summary>
    public class ReuseWeakReference : IReuseWrapper
    {
        /// <summary>Provides access to <see cref="WeakReference"/> members.</summary>
        public readonly WeakReference Ref;

        /// <summary>Wrapped value, delegates to <see cref="WeakReference.Target"/></summary>
        public object Target { get { return Ref.Target; } }

        /// <summary>Wraps input target into weak reference</summary> <param name="value">Value to wrap.</param>
        public ReuseWeakReference(object value)
        {
            Ref = new WeakReference(value);
        }
    }

    /// <summary>Wraps reused value ref box with ability to Swap it new value. Similar to <see cref="Ref{T}"/>.</summary>
    public sealed class ReuseSwapable : IReuseWrapper
    {
        /// <summary>Wrapped value.</summary>
        public object Target { get { return _value; } }

        /// <summary>Constructs ref wrapper.</summary> <param name="value">Wrapped value.</param>
        public ReuseSwapable(object value)
        {
            _value = value;
        }

        /// <summary>Exchanges currently hold object with <paramref name="getValue"/> result.</summary>
        /// <param name="getValue">Delegate to produce new object value from current one passed as parameter.</param>
        /// <returns>Returns old object value the same way as <see cref="Interlocked.Exchange(ref int,int)"/></returns>
        /// <remarks>Important: <paramref name="getValue"/> delegate may be called multiple times with new value each time, 
        /// if it was changed in meantime by other concurrently running code.</remarks>
        public object Swap(Func<object, object> getValue)
        {
            return Ref.Swap(ref _value, getValue);
        }

        /// <summary>Simplified version of Swap ignoring old value.</summary> <param name="newValue">New value.</param> <returns>Old value.</returns>
        public object Swap(object newValue)
        {
            return Interlocked.Exchange(ref _value, newValue);
        }

        private object _value;
    }

    /// <summary>If recycled set to True, that command Scope to create and return new value on next access.</summary>
    public interface IRecyclable
    {
        /// <summary>Indicates that value should be recycled.</summary>
        bool IsRecycled { get; }

        /// <summary>Commands to recycle value.</summary>
        void Recycle();
    }

    /// <summary>Wraps value with ability to be recycled, so next access to recycle value with create new value from Container.</summary>
    public class ReuseRecyclable : IReuseWrapper, IRecyclable
    {
        /// <summary>Wraps input value</summary> <param name="value"></param>
        public ReuseRecyclable(object value)
        {
            _value = value;
        }

        // Returns wrapped value.
        public object Target
        {
            get { return _value; }
        }

        /// <summary>Indicates that value should be recycled.</summary>
        public bool IsRecycled { get; private set; }

        /// <summary>Commands to recycle value.</summary>
        public void Recycle()
        {
            IsRecycled = true;
        }

        private readonly object _value;
    }

    /// <summary>Specifies what to return when <see cref="IResolver"/> unable to resolve service.</summary>
    public enum IfUnresolved
    {
        /// <summary>Specifies to throw <see cref="ContainerException"/> if no service found.</summary>
        Throw, 
        /// <summary>Specifies to return default value instead of throwing error.</summary>
        ReturnDefault
    }

    /// <summary>Declares minimal API for service resolution.
    /// The user friendly convenient methods are implemented as extension methods in <see cref="Resolver"/> class.</summary>
    /// <remarks>Resolve default and keyed is separated because of micro optimization for faster resolution.</remarks>
    public interface IResolver
    {
        /// <summary>Resolves service from container and returns created service object.</summary>
        /// <param name="serviceType">Service type to search and to return.</param>
        /// <param name="ifUnresolved">Says what to do if service is unresolved.</param>
        /// <param name="parentOrEmpty">Parent request for dependency, or null for resolution root.</param>
        /// <returns>Created service object or default based on <paramref name="ifUnresolved"/> provided.</returns>
        object ResolveDefault(Type serviceType, IfUnresolved ifUnresolved, Request parentOrEmpty);

        /// <summary>Resolves service from container and returns created service object.</summary>
        /// <param name="serviceType">Service type to search and to return.</param>
        /// <param name="serviceKey">Optional service key used for registering service.</param>
        /// <param name="ifUnresolved">Says what to do if service is unresolved.</param>
        /// <param name="requiredServiceType">Actual registered service type to use instead of <paramref name="serviceType"/>, 
        /// or wrapped type for generic wrappers.  The type should be assignable to return <paramref name="serviceType"/>.</param>
        /// <param name="parentOrEmpty">Parent request for dependency, or null for resolution root.</param>
        /// <returns>Created service object or default based on <paramref name="ifUnresolved"/> provided.</returns>
        /// <remarks>
        /// This method covers all possible resolution input parameters comparing to <see cref="ResolveDefault"/>, and
        /// by specifying the same parameters as for <see cref="ResolveDefault"/> should return the same result.
        /// </remarks>
        object ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved, Type requiredServiceType, Request parentOrEmpty);

        /// <summary>For given instance resolves and sets properties and fields.
        /// It respects <see cref="DryIoc.Rules.PropertiesAndFields"/> rules set per container, 
        /// or if rules are not set it uses default rule <see cref="PropertiesAndFields.PublicNonPrimitive"/>, 
        /// or you can specify your own rules with <paramref name="selectPropertiesAndFields"/> parameter.</summary>
        /// <param name="instance">Service instance with properties to resolve and initialize.</param>
        /// <param name="selectPropertiesAndFields">(optional) Function to select properties and fields, overrides all other rules if specified.</param>
        /// <param name="parentOrEmpty">Parent request for dependency, or null for resolution root.</param>
        /// <remarks>Different Rules could be combined together using <see cref="PropertiesAndFields.OverrideWith"/> method.</remarks>        
        void ResolvePropertiesAndFields(object instance, PropertiesAndFieldsSelector selectPropertiesAndFields, Request parentOrEmpty);

        /// <summary>Resolve all services registered for specified <paramref name="serviceType"/>, or if not found returns
        /// empty enumerable. If <paramref name="serviceType"/> specified then returns only (single) service registered with
        /// this type. Excludes for result composite parent identified by <paramref name="compositeParentKey"/>.</summary>
        /// <param name="serviceType">Return type of an service item.</param>
        /// <param name="serviceKey">(optional) Resolve only single service registered with the key.</param>
        /// <param name="requiredServiceType">(optional) Actual registered service to search for.</param>
        /// <param name="compositeParentKey">(optional) Parent service key to exclude to support Composite pattern.</param>
        /// <returns>Enumerable of found services or empty. Does Not throw if no service found.</returns>
        IEnumerable<object> ResolveMany(Type serviceType, object serviceKey, Type requiredServiceType, object compositeParentKey);
    }

    /// <summary>Specifies options to handle situation when registering some service already present in the registry.</summary>
    public enum IfAlreadyRegistered
    {
        /// <summary>Appends new default registration or throws registration with the same key.</summary>
        AppendDefault,
        /// <summary>Throws if default or registration with the same key is already exist.</summary>
        Throw,
        /// <summary>Keeps old default or keyed registration ignoring new registration: ensures Register-Once semantics.</summary>
        KeepIt,
        /// <summary>Updates old registration with one.</summary>
        Update
    }

    /// <summary>Defines operations that for changing registry, and checking if something exist in registry.</summary>
    public interface IRegistrator
    {
        /// <summary>Registers factory in registry with specified service type and key for lookup.</summary>
        /// <param name="factory">To register.</param>
        /// <param name="serviceType">Service type as unique key in registry for lookup.</param>
        /// <param name="serviceKey">Service key as complementary lookup for the same service type.</param>
        /// <param name="ifAlreadyRegistered">Policy how to deal with already registered factory with same service type and key.</param>
        void Register(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered);

        /// <summary>Returns true if expected factory is registered with specified service key and type.</summary>
        /// <param name="serviceType">Type to lookup.</param>
        /// <param name="serviceKey">Key to lookup for the same type.</param>
        /// <param name="factoryType">Expected factory type.</param>
        /// <param name="condition">Expected factory condition.</param>
        /// <returns>True if expected factory found in registry.</returns>
        bool IsRegistered(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition);

        /// <summary>Removes factory with specified service type and key from registry.</summary>
        /// <param name="serviceType">Type to lookup.</param>
        /// <param name="serviceKey">Key to lookup for the same type.</param>
        /// <param name="factoryType">Expected factory type.</param>
        /// <param name="condition">Expected factory condition.</param>
        void Unregister(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition);
    }

    /// <summary>Provides access to both resolver and scopes to <see cref="FactoryDelegate"/>.</summary>
    public interface IResolverWithScopes : IResolver
    {
        /// <summary>Scope associated with container.</summary>
        IScope SingletonScope { get; }

        /// <summary>Scope associated with containers created by <see cref="Container.OpenScope"/>.
        /// If container is not created by <see cref="Container.OpenScope"/> then it is the same as <see cref="SingletonScope"/>.</summary>
        IScope CurrentScope { get; }
    }

    /// <summary>Exposes operations required for internal registry access. 
    /// That's why most of them are implemented explicitly by <see cref="Container"/>.</summary>
    public interface IContainer : IRegistrator, IResolverWithScopes, IDisposable
    {
        /// <summary>Empty request bound to container. All other requests are created by pushing to empty request.</summary>
        Request EmptyRequest { get; }

        /// <summary>Returns true if container is disposed.</summary>
        bool IsDisposed { get; }

        /// <summary>Self weak reference, with readable message when container is GCed/Disposed.</summary>
        ContainerWeakRef ContainerWeakRef { get; }

        /// <summary>Rules for defining resolution/registration behavior throughout container.</summary>
        Rules Rules { get; }

        /// <summary>Closure for objects required for <see cref="FactoryDelegate"/> invocation.
        /// Accumulates the objects, but could be dropped off without an issue, like cache.</summary>
        ResolutionStateCache ResolutionStateCache { get; }

        /// <summary>Copies all of container state except Cache and specifies new rules.</summary>
        /// <param name="configure">(optional) Configure rules, if not specified then uses Rules from current container.</param> 
        /// <param name="scopeContext">(optional) New scope context, if not specified then uses context from current container.</param>
        /// <returns>New container.</returns>
        IContainer With(Func<Rules, Rules> configure = null, IScopeContext scopeContext = null);

        /// <summary>Returns new container with all expression, delegate, items cache removed/reset.
        /// It will preserve resolved services in Singleton/Current scope.</summary>
        /// <returns>New container with empty cache.</returns>
        IContainer WithoutCache();

        /// <summary>Creates new container with whole state shared with original except singletons.</summary>
        /// <returns>New container with empty Singleton Scope.</returns>
        IContainer WithoutSingletonsAndCache();

        /// <summary>Shares all parts with original container But copies registration, so the new registration
        /// won't be visible in original. Registrations include decorators and wrappers as well.</summary>
        /// <param name="preserveCache">(optional) If set preserves cache if you know what to do.</param>
        /// <returns>New container with copy of all registrations.</returns>
        IContainer WithRegistrationsCopy(bool preserveCache = false);

        /// <summary>Creates new container with new opened scope and set this scope as current in provided/inherited context.</summary>
        /// <param name="name">(optional) Name for opened scope to allow reuse to identify the scope.</param>
        /// <param name="configure">(optional) Configure rules, if not specified then uses Rules from current container.</param> 
        /// <returns>New container with different current scope.</returns>
        /// <example><code lang="cs"><![CDATA[
        /// using (var scoped = container.OpenScope())
        /// {
        ///     var handler = scoped.Resolve<IHandler>();
        ///     handler.Handle(data);
        /// }
        /// ]]></code></example>
        IContainer OpenScope(object name = null, Func<Rules, Rules> configure = null);

        /// <summary>Creates child container using the same rules as its created from.
        /// Additionally child container will fallback for not registered service to it parent.</summary>
        /// <param name="shareSingletons">If set allow to share singletons from parent container.</param>
        /// <returns>New child container.</returns>
        IContainer CreateChildContainer(bool shareSingletons = false);

        /// <summary>Searches for requested factory in registry, and then using <see cref="DryIoc.Rules.UnknownServiceResolvers"/>.</summary>
        /// <param name="request">Factory lookup info.</param>
        /// <returns>Found factory, otherwise null if <see cref="Request.IfUnresolved"/> is set to <see cref="IfUnresolved.ReturnDefault"/>.</returns>
        Factory ResolveFactory(Request request);

        /// <summary>Searches for registered service factory and returns it, or null if not found.</summary>
        /// <param name="serviceType">Service type to look for.</param>
        /// <param name="serviceKey">(optional) Service key to lookup in addition to type.</param>
        /// <returns>Found registered factory or null.</returns>
        Factory GetServiceFactoryOrDefault(Type serviceType, object serviceKey);

        /// <summary>Searches for registered wrapper factory and returns it, or null if not found.</summary>
        /// <param name="serviceType">Service type to look for.</param> <returns>Found wrapper factory or null.</returns>
        Factory GetWrapperFactoryOrDefault(Type serviceType);

        /// <summary>Creates decorator expression: it could be either Func{TService,TService}, 
        /// or service expression for replacing decorators.</summary>
        /// <param name="request">Decorated service request.</param>
        /// <returns>Decorator expression.</returns>
        Expression GetDecoratorExpressionOrDefault(Request request);

        /// <summary>Finds all registered default and keyed service factories and returns them.
        /// It skips decorators and wrappers.</summary>
        /// <param name="serviceType"></param>
        /// <returns></returns>
        IEnumerable<KV<object, Factory>> GetAllServiceFactories(Type serviceType);

        /// <summary>If <paramref name="type"/> is generic type then this method checks if the type registered as generic wrapper,
        /// and recursively unwraps and returns its type argument. This type argument is the actual service type we want to find.
        /// Otherwise, method returns the input <paramref name="type"/>.</summary>
        /// <param name="type">Type to unwrap. Method will return early if type is not generic.</param>
        /// <returns>Unwrapped service type in case it corresponds to registered generic wrapper, or input type in all other cases.</returns>
        Type UnwrapServiceType(Type type);
    }

    /// <summary>Resolves all registered services of <typeparamref name="TService"/> type on demand, 
    /// when enumerator <see cref="IEnumerator.MoveNext"/> called. If service type is not found, empty returned.</summary>
    /// <typeparam name="TService">Service type to resolve.</typeparam>
    public sealed class LazyEnumerable<TService> : IEnumerable<TService>
    {
        /// <summary>Exposes internal items enumerable.</summary>
        public readonly IEnumerable<TService> Items;

        /// <summary>Wraps lazy resolved items.</summary> <param name="items">Lazy resolved items.</param>
        public LazyEnumerable(IEnumerable<TService> items)
        {
            Items = items.ThrowIfNull();
        }

        /// <summary>Return items enumerator.</summary> <returns>items enumerator.</returns>
        public IEnumerator<TService> GetEnumerator()
        {
            return Items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }

    /// <summary>Wrapper type to box service with associated arbitrary metadata object.</summary>
    /// <typeparam name="T">Service type.</typeparam>
    /// <typeparam name="TMetadata">Arbitrary metadata object type.</typeparam>
    public sealed class Meta<T, TMetadata>
    {
        /// <summary>Value or object with associated metadata.</summary>
        public readonly T Value;

        /// <summary>Associated metadata object. Could be anything.</summary>
        public readonly TMetadata Metadata;

        /// <summary>Boxes value and its associated metadata together.</summary>
        /// <param name="value">value</param> <param name="metadata">any metadata object</param>
        public Meta(T value, TMetadata metadata)
        {
            Value = value;
            Metadata = metadata;
        }
    }

    /// <summary>Used to wrap resolution scope together with directly resolved service value.
    /// Disposing wrapper means disposing service (if disposable) and disposing scope (all reused disposable dependencies.)</summary>
    /// <typeparam name="T">Type of resolved service.</typeparam>
    public sealed class ResolutionScoped<T> : IDisposable
    {
        /// <summary>Resolved service.</summary>
        public T Value { get; private set; }

        /// <summary>Exposes resolution scope. The supported operation for it is <see cref="IDisposable.Dispose"/>.
        /// So you can dispose scope separately from resolved service.</summary>
        public IDisposable Scope { get; private set; }

        /// <summary>Creates wrapper</summary>
        /// <param name="value">Resolved service.</param> <param name="scope">Resolution root scope.</param>
        public ResolutionScoped(T value, IScope scope)
        {
            Value = value;
            Scope = scope as IDisposable;
        }

        /// <summary>Disposes both resolved service (if disposable) and then disposes resolution scope.</summary>
        public void Dispose()
        {
            var disposableValue = Value as IDisposable;
            if (disposableValue != null)
            {
                disposableValue.Dispose();
                Value = default(T);
            }

            if (Scope != null)
            {
                Scope.Dispose();
                Scope = null;
            }
        }
    }

    /// <summary>Wraps factory expression created by container internally. May be used for debugging.</summary>
    /// <typeparam name="TService">Service type to resolve.</typeparam>
    [DebuggerDisplay("{Expression}")]
    public sealed class FactoryExpression<TService>
    {
        /// <summary>Factory expression that Container compiles to delegate.</summary>
        public readonly Expression<FactoryDelegate> Value;

        /// <summary>Creates wrapper.</summary> <param name="value">Wrapped expression.</param>
        public FactoryExpression(Expression<FactoryDelegate> value)
        {
            Value = value;
        }
    }

    /// <summary>Exception that container throws in case of error. Dedicated exception type simplifies
    /// filtering or catching container relevant exceptions from client code.</summary>
    [SuppressMessage("Microsoft.Usage", "CA2237:MarkISerializableTypesWithSerializable")]
    public class ContainerException : InvalidOperationException
    {
        /// <summary>Error code of exception, possible values are listed in <see cref="Error"/> class.</summary>
        public readonly int Error;

        /// <summary>Creates exception by wrapping <paramref name="errorCode"/> and its message,
        /// optionally with <paramref name="inner"/> exception.</summary>
        /// <param name="errorCheck">Type of check</param>
        /// <param name="errorCode">Error code, check <see cref="Error"/> for possible values.</param>
        /// <param name="arg0">(optional) Arguments for formatted message.</param> <param name="arg1"></param> <param name="arg2"></param> <param name="arg3"></param>
        /// <param name="inner">(optional) Inner exception.</param>
        /// <returns>Created exception.</returns>
        public static ContainerException Of(ErrorCheck errorCheck, int errorCode,
            object arg0, object arg1 = null, object arg2 = null, object arg3 = null,
            Exception inner = null)
        {
            string message = null;
            if (errorCode != -1)
                message = string.Format(DryIoc.Error.Messages[errorCode], Print(arg0), Print(arg1), Print(arg2), Print(arg3));
            else
            {
                switch (errorCheck) // handle error check when error code is unspecified.
                {
                    case ErrorCheck.InvalidCondition:
                        errorCode = DryIoc.Error.INVALID_CONDITION;
                        message = string.Format(DryIoc.Error.Messages[errorCode], Print(arg0), Print(arg0.GetType()));
                        break;
                    case ErrorCheck.IsNull:
                        errorCode = DryIoc.Error.IS_NULL;
                        message = string.Format(DryIoc.Error.Messages[errorCode], Print(arg0));
                        break;
                    case ErrorCheck.IsNotOfType:
                        errorCode = DryIoc.Error.IS_NOT_OF_TYPE;
                        message = string.Format(DryIoc.Error.Messages[errorCode], Print(arg0), Print(arg1));
                        break;
                    case ErrorCheck.TypeIsNotOfType:
                        errorCode = DryIoc.Error.TYPE_IS_NOT_OF_TYPE;
                        message = string.Format(DryIoc.Error.Messages[errorCode], Print(arg0), Print(arg1));
                        break;
                }
            }

            return inner == null
                ? new ContainerException(errorCode, message)
                : new ContainerException(errorCode, message, inner);
        }

        /// <summary>Creates exception with message describing cause and context of error.</summary>
        /// <param name="error"></param>
        /// <param name="message">Error message.</param>
        protected ContainerException(int error, string message)
            : base(message)
        {
            Error = error;
        }

        /// <summary>Creates exception with message describing cause and context of error,
        /// and leading/system exception causing it.</summary>
        /// <param name="error"></param>
        /// <param name="message">Error message.</param>
        /// <param name="innerException">Underlying system/leading exception.</param>
        protected ContainerException(int error, string message, Exception innerException)
            : base(message, innerException)
        {
            Error = error;
        }

        /// <summary>Prints argument for formatted message.</summary> <param name="arg">To print.</param> <returns>Printed string.</returns>
        protected static string Print(object arg)
        {
            return new StringBuilder().Print(arg).ToString();
        }
    }

    /// <summary>Defines error codes and error messages for all DryIoc exceptions (DryIoc extensions may define their own.)</summary>
    public static class Error
    {
        /// <summary>First error code to identify error range for other possible error code definitions.</summary>
        public readonly static int FIRST_ERROR_CODE = 0;

        /// <summary>List of error messages indexed with code.</summary>
        public readonly static IList<string> Messages = new List<string>(100);

#pragma warning disable 1591 // Missing XML-comment
        public static readonly int
            INVALID_CONDITION =     Of("Argument {0} of type {1} has invalid condition."),
            IS_NULL =               Of("Argument of type {0} is null."),
            IS_NOT_OF_TYPE =        Of("Argument {0} is not of type {1}."),
            TYPE_IS_NOT_OF_TYPE =   Of("Type argument {0} is not assignable from type {1}."),

            UNABLE_TO_RESOLVE_SERVICE = Of("Unable to resolve {0}." + Environment.NewLine
                                                                + "Please register service, or specify @requiredServiceType while resolving, or add Rules.WithUnknownServiceResolver(MyRule)."),
            EXPECTED_SINGLE_DEFAULT_FACTORY = Of("Expecting single default registration of {0} but found many:" + Environment.NewLine
                                                                + "{1}." + Environment.NewLine
                                                                + "Please identify service with key, or metadata, or use Rules.WithFactorySelector to specify single registered factory."),
            IMPL_NOT_ASSIGNABLE_TO_SERVICE_TYPE = Of("Implementation type {0} should be assignable to service type {1} but it is not."),
            REG_OPEN_GENERIC_REQUIRE_FACTORY_PROVIDER = Of("Unable to register not a factory provider for open-generic service {0}."),
            REG_OPEN_GENERIC_IMPL_WITH_NON_GENERIC_SERVICE = Of("Unable to register open-generic implementation {0} with non-generic service {1}."),
            REG_OPEN_GENERIC_SERVICE_WITH_MISSING_TYPE_ARGS = Of("Unable to register open-generic implementation {0} because service {1} should specify all of its type arguments, but specifies only {2}."),
            REG_NOT_A_GENERIC_TYPEDEF_IMPL_TYPE = Of("Unsupported registration of implementation {0} which is not a generic type definition but contains generic parameters." + Environment.NewLine
                                                                + "Consider to register generic type definition {1} instead."),
            REG_NOT_A_GENERIC_TYPEDEF_SERVICE_TYPE = Of("Unsupported registration of service {0} which is not a generic type definition but contains generic parameters." + Environment.NewLine
                                                                + "Consider to register generic type definition {1} instead."),
            EXPECTED_NON_ABSTRACT_IMPL_TYPE = Of("Expecting not abstract and not interface implementation type, but found {0}."),
            NO_PUBLIC_CONSTRUCTOR_DEFINED = Of("There is no public constructor defined for {0}."),
            NO_CTOR_SELECTOR_FOR_IMPL_WITH_MULTIPLE_CTORS = Of("Unspecified how to select single constructor for implementation type {0} with {1} public constructors."),
            NOT_MATCHED_IMPL_BASE_TYPES_WITH_SERVICE_TYPE = Of("Unable to match service with open-generic {0} implementing {1} when resolving {2}."),
            CTOR_IS_MISSING_SOME_PARAMETERS = Of("Constructor [{0}] of {1} misses some arguments required for {2} dependency."),
            UNABLE_TO_SELECT_CTOR = Of("Unable to select single constructor from {0} available in {1}." + Environment.NewLine
                                                                + "Please provide constructor selector when registering service."),
            EXPECTED_FUNC_WITH_MULTIPLE_ARGS = Of("Expecting Func with one or more arguments but found {0}."),
            EXPECTED_CLOSED_GENERIC_SERVICE_TYPE = Of("Expecting closed-generic service type but found {0}."),
            RECURSIVE_DEPENDENCY_DETECTED = Of("Recursive dependency is detected when resolving" + Environment.NewLine + "{0}."),
            SCOPE_IS_DISPOSED = Of("Scope is disposed and scoped instances are no longer available."),
            WRAPPER_CAN_WRAP_SINGLE_SERVICE_ONLY = Of("Wrapper {0} can wrap single service type only, but found many. You should specify service type selector in wrapper setup."),
            NOT_FOUND_OPEN_GENERIC_IMPL_TYPE_ARG_IN_SERVICE = Of("Unable to find for open-generic implementation {0} the type argument {1} when resolving {2}."),
            UNABLE_TO_SELECT_CTOR_USING_SELECTOR = Of("Unable to get constructor of {0} using provided constructor selector."),
            UNABLE_TO_FIND_CTOR_WITH_ALL_RESOLVABLE_ARGS = Of("Unable to find constructor with all resolvable parameters when resolving {0}."),
            UNABLE_TO_FIND_MATCHING_CTOR_FOR_FUNC_WITH_ARGS = Of("Unable to find constructor with all parameters matching Func signature {0} " + Environment.NewLine
                                                                + "and the rest of parameters resolvable from Container when resolving: {1}."),
            REGED_FACTORY_DLG_RESULT_NOT_OF_SERVICE_TYPE = Of("Registered factory delegate returns service {0} is not assignable to {2}."),
            INJECTED_VALUE_IS_OF_DIFFERENT_TYPE = Of("Injected value {0} is not assignable to {2}."),
            REGED_OBJ_NOT_ASSIGNABLE_TO_SERVICE_TYPE = Of("Registered instance {0} is not assignable to serviceType {1}."),
            NOT_FOUND_SPECIFIED_WRITEABLE_PROPERTY_OR_FIELD = Of("Unable to find writable property or field \"{0}\" when resolving: {1}."),
            NO_SERVICE_TYPE_TO_REGISTER_ALL = Of("Unable to register any of implementation {0} implemented services {1}."),
            PUSHING_TO_REQUEST_WITHOUT_FACTORY = Of("Pushing next info {0} to request not yet resolved to factory: {1}"),
            TARGET_WAS_ALREADY_DISPOSED = Of("Target {0} was already disposed in {1} wrapper."),
            NOT_MATCHED_GENERIC_PARAM_CONSTRAINTS = Of("Service type does not match registered open-generic implementation constraints {0} when resolving {1}."),
            NON_GENERIC_WRAPPER_NO_WRAPPED_TYPE_SPECIFIED = Of("Non-generic wrapper {0} should specify wrapped service selector when registered."),
            DEPENDENCY_HAS_SHORTER_REUSE_LIFESPAN = Of("Dependency {0} has shorter Reuse lifespan than its parent: {1}." + Environment.NewLine
                                                                + "{2} lifetime is shorter than {3}." + Environment.NewLine
                                                                + "You may turn Off this error with new Container(rules=>rules.EnableThrowIfDepenedencyHasShorterReuseLifespan(false))."),
            WEAKREF_REUSE_WRAPPER_GCED = Of("Service with WeakReference reuse wrapper is garbage collected now, and no longer available."),
            INSTANCE_FACTORY_IS_NULL = Of("Instance factory is null when resolving: {0}"),
            SERVICE_IS_NOT_ASSIGNABLE_FROM_FACTORY_METHOD = Of("Service of {0} is not assignable from factory method {2} when resolving: {3}."),
            FACTORY_OBJ_IS_NULL_IN_FACTORY_METHOD = Of("Unable to use null factory object with factory method {0} when resolving: {1}."),
            FACTORY_OBJ_PROVIDED_BUT_METHOD_IS_STATIC = Of("Factory instance provided {0} But factory method is static {1} when resolving: {2}."),
            NO_OPEN_THREAD_SCOPE = Of("Unable to find open thread scope in {0}. Please OpenScope with {0} to make sure thread reuse work."),
            CONTAINER_IS_GARBAGE_COLLECTED = Of("Container is no longer available (has been garbage-collected)."),
            CANT_CREATE_DECORATOR_EXPR = Of("Unable to create decorator expression for: {0}."),
            UNABLE_TO_REGISTER_DUPLICATE_DEFAULT = Of("Service {0} without key is already registered as {2}."),
            UNABLE_TO_REGISTER_DUPLICATE_KEY = Of("Service {0} with the same key \"{1}\" is already registered as {2}."),
            NO_CURRENT_SCOPE = Of("No current scope available: probably you are resolving scoped service outside of scope."),
            CONTAINER_IS_DISPOSED = Of("Container {0} is disposed and its operations are no longer available."),
            UNABLE_TO_DISPOSE_NOT_A_CURRENT_SCOPE = Of("Unable to dispose not a current opened scope."),
            NOT_DIRECT_SCOPE_PARENT = Of("Unable to Open Scope from not a direct parent container."),
            CANT_RESOLVE_REUSE_WRAPPER = Of("Unable to resolve reuse wrapper {0} for: {1}"),
            WRAPPED_NOT_ASSIGNABLE_FROM_REQUIRED_TYPE = Of("Service (wrapped) type {0} is not assignable from required service type {1} when resolving {2}."),
            NO_MATCHED_SCOPE_FOUND = Of("Unable to find scope with matching name \"{0}\" in current scope reuse."),
            UNABLE_TO_NEW_OPEN_GENERIC = Of("Unable to New not concrete/open-generic type {0}."),
            REG_REUSED_OBJ_WRAPPER_IS_NOT_IREUSED = Of("Registered reused object wrapper at index {0} of {1} does not implement expected {2} interface."),
            RECYCLABLE_REUSE_WRAPPER_IS_RECYCLED = Of("Recyclable wrapper is recycled.");
#pragma warning restore 1591

        public static int Of(string message)
        {
            Messages.Add(message);
            return FIRST_ERROR_CODE + Messages.Count - 1;
        }
    }

    /// <summary>Checked error condition, possible error sources.</summary>
    public enum ErrorCheck
    {
        /// <summary>Unspecified, just throw.</summary>
        Unspecified,
        /// <summary>Predicate evaluated to false.</summary>
        InvalidCondition,
        /// <summary>Checked object is null.</summary>
        IsNull,
        /// <summary>Checked object is of unexpected type.</summary>
        IsNotOfType,
        /// <summary>Checked type is not assignable to expected type</summary>
        TypeIsNotOfType,
        /// <summary>Invoked operation throw, it is source of inner exception.</summary>
        OperationThrows,
    }

    /// <summary>Enables more clean error message formatting and a bit of code contracts.</summary>
    public static partial class Throw
    {
        /// <summary>Declares mapping between <see cref="ErrorCheck"/> type and <paramref name="error"/> code to specific <see cref="Exception"/>.</summary>
        /// <returns>Returns mapped exception.</returns>
        public delegate Exception GetMatchedExceptionHandler(ErrorCheck errorCheck, int error, object arg0, object arg1, object arg2, object arg3, Exception inner);

        /// <summary>Returns matched exception (to check type and error code). By default return <see cref="ContainerException"/>.</summary>
        public static GetMatchedExceptionHandler GetMatchedException = ContainerException.Of;

        /// <summary>Throws matched exception if throw condition is true.</summary>
        /// <param name="throwCondition">Condition to be evaluated, throws if result is true, otherwise - does nothing.</param>
        /// <param name="error">Error code to match to exception thrown.</param>
        /// <param name="arg0">Arguments to formatted message.</param> <param name="arg1"></param> <param name="arg2"></param> <param name="arg3"></param>
        public static void If(bool throwCondition, int error = -1, object arg0 = null, object arg1 = null, object arg2 = null, object arg3 = null)
        {
            if (!throwCondition) return;
            throw GetMatchedException(ErrorCheck.InvalidCondition, error, arg0, arg1, arg2, arg3, null);
        }

        /// <summary>Throws matched exception if throw condition is true. Otherwise return source <paramref name="arg0"/>.</summary>
        /// <typeparam name="T">Type of source <paramref name="arg0"/>.</typeparam>
        /// <param name="arg0">In case of exception <paramref name="arg0"/> will be used as first argument in formatted message.</param>
        /// <param name="throwCondition">Condition to be evaluated, throws if result is true, otherwise - does nothing.</param>
        /// <param name="error">Error code to match to exception thrown.</param>
        /// <param name="arg1">Rest of arguments to formatted message.</param> <param name="arg2"></param> <param name="arg3"></param>
        /// <returns><paramref name="arg0"/> if throw condition is false.</returns>
        public static T ThrowIf<T>(this T arg0, bool throwCondition, int error = -1, object arg1 = null, object arg2 = null, object arg3 = null)
        {
            if (!throwCondition) return arg0;
            throw GetMatchedException(ErrorCheck.InvalidCondition, error, arg0, arg1, arg2, arg3, null);
        }

        /// <summary>Throws matched exception if throw condition is true. Passes <see cref="arg0"/> to condition. 
        /// Enables fluent syntax at cast of delegate creation. Otherwise return source <paramref name="arg0"/>.</summary>
        /// <typeparam name="T">Type of source <paramref name="arg0"/>.</typeparam>
        /// <param name="arg0">In case of exception <paramref name="arg0"/> will be used as first argument in formatted message.</param>
        /// <param name="throwCondition">Condition to be evaluated, throws if result is true, otherwise - does nothing.</param>
        /// <param name="error">Error code to match to exception thrown.</param>
        /// <param name="arg1">Rest of arguments to formatted message.</param> <param name="arg2"></param> <param name="arg3"></param>
        /// <returns><paramref name="arg0"/> if throw condition is false.</returns>
        public static T ThrowIf<T>(this T arg0, Func<T, bool> throwCondition, int error = -1, object arg1 = null, object arg2 = null, object arg3 = null)
        {
            if (!throwCondition(arg0)) return arg0;
            throw GetMatchedException(ErrorCheck.InvalidCondition, error, arg0, arg1, arg2, arg3, null);
        }

        public static T ThrowIfNull<T>(this T arg, int error = -1, object arg0 = null, object arg1 = null, object arg2 = null, object arg3 = null)
            where T : class
        {
            if (arg != null) return arg;
            throw GetMatchedException(ErrorCheck.IsNull, error, arg0 ?? typeof(T), arg1, arg2, arg3, null);
        }

        public static T ThrowIfNotOf<T>(this T arg0, Type arg1, int error = -1, object arg2 = null, object arg3 = null)
            where T : class
        {
            if (arg1.IsTypeOf(arg0)) return arg0;
            throw GetMatchedException(ErrorCheck.IsNotOfType, error, arg0, arg1, arg2, arg3, null);
        }

        public static Type ThrowIfNotOf(this Type arg0, Type arg1, int error = -1, object arg2 = null, object arg3 = null)
        {
            if (arg1.IsAssignableTo(arg0)) return arg0;
            throw GetMatchedException(ErrorCheck.TypeIsNotOfType, error, arg0, arg1, arg2, arg3, null);
        }

        public static T IfThrows<TEx, T>(Func<T> operation, int error, object arg0 = null, object arg1 = null,
            object arg2 = null, object arg3 = null) where TEx : Exception
        {
            try { return operation(); }
            catch (TEx ex) { throw GetMatchedException(ErrorCheck.OperationThrows, error, arg0, arg1, arg2, arg3, ex); }
        }

        public static void Error(int error, object arg0 = null, object arg1 = null, object arg2 = null, object arg3 = null)
        {
            throw GetMatchedException(ErrorCheck.Unspecified, error, arg0, arg1, arg2, arg3, null);
        }

        /// <summary>Throws <paramref name="error"/> instead of returning value of <typeparamref name="T"/>. 
        /// Supposed to be used in expression that require some return value.</summary>
        /// <typeparam name="T"></typeparam> <param name="error"></param>
        /// <param name="arg0"></param> <param name="arg1"></param> <param name="arg2"></param> <param name="arg3"></param>
        /// <returns>Does not return, throws instead.</returns>
        public static T Instead<T>(int error, object arg0 = null, object arg1 = null, object arg2 = null, object arg3 = null)
        {
            throw GetMatchedException(ErrorCheck.Unspecified, error, arg0, arg1, arg2, arg3, null);
        }
    }

    /// <summary>Contains helper methods to work with Type: for instance to find Type implemented base types and interfaces, etc.</summary>
    public static class TypeTools
    {
        /// <summary>Flags for <see cref="GetImplementedTypes"/> method.</summary>
        [Flags]
        public enum IncludeFlags { None = 0, SourceType = 1, ObjectType = 2 }

        /// <summary>Returns all interfaces and all base types (in that order) implemented by <paramref name="sourceType"/>.
        /// Specify <paramref name="includeFlags"/> to include <paramref name="sourceType"/> itself as first item and 
        /// <see cref="object"/> type as the last item.</summary>
        /// <param name="sourceType">Source type for discovery.</param>
        /// <param name="includeFlags">Additional types to include into result collection.</param>
        /// <returns>Collection of found types.</returns>
        public static Type[] GetImplementedTypes(this Type sourceType, IncludeFlags includeFlags = IncludeFlags.None)
        {
            Type[] results;

            var interfaces = sourceType.GetImplementedInterfaces();
            var interfaceStartIndex = (includeFlags & IncludeFlags.SourceType) == 0 ? 0 : 1;
            var includingObjectType = (includeFlags & IncludeFlags.ObjectType) == 0 ? 0 : 1;
            var sourcePlusInterfaceCount = interfaceStartIndex + interfaces.Length;

            var baseType = sourceType.GetTypeInfo().BaseType;
            if (baseType == null || baseType == typeof(object))
                results = new Type[sourcePlusInterfaceCount + includingObjectType];
            else
            {
                List<Type> baseBaseTypes = null;
                for (var bb = baseType.GetTypeInfo().BaseType; bb != null && bb != typeof(object); bb = bb.GetTypeInfo().BaseType)
                    (baseBaseTypes ?? (baseBaseTypes = new List<Type>(2))).Add(bb);

                if (baseBaseTypes == null)
                    results = new Type[sourcePlusInterfaceCount + includingObjectType + 1];
                else
                {
                    results = new Type[sourcePlusInterfaceCount + baseBaseTypes.Count + includingObjectType + 1];
                    baseBaseTypes.CopyTo(results, sourcePlusInterfaceCount + 1);
                }

                results[sourcePlusInterfaceCount] = baseType;
            }

            if (interfaces.Length == 1)
                results[interfaceStartIndex] = interfaces[0];
            else if (interfaces.Length > 1)
                Array.Copy(interfaces, 0, results, interfaceStartIndex, interfaces.Length);

            if (interfaceStartIndex == 1)
                results[0] = sourceType;
            if (includingObjectType == 1)
                results[results.Length - 1] = typeof(object);

            return results;
        }

        /// <summary>Gets a collection of the interfaces implemented by the current type and its base types.</summary>
        /// <param name="type">Source type</param>
        /// <returns>Collection of interface types.</returns>
        public static Type[] GetImplementedInterfaces(this Type type)
        {
            return type.GetTypeInfo().ImplementedInterfaces.ToArrayOrSelf();
        }

        /// <summary>Returns true if <paramref name="type"/> contains all generic parameters from <paramref name="genericParameters"/>.</summary>
        /// <param name="type">Expected to be open-generic type.</param>
        /// <param name="genericParameters">Generic parameter type to look in.</param>
        /// <returns>Returns true if contains and false otherwise.</returns>
        public static bool ContainsAllGenericParameters(this Type type, Type[] genericParameters)
        {
            if (!type.IsOpenGeneric())
                return false;

            var paramNames = new string[genericParameters.Length];
            for (var i = 0; i < genericParameters.Length; i++)
                paramNames[i] = genericParameters[i].Name;

            SetNamesFoundInGenericParametersToNull(paramNames, type.GetGenericParamsAndArgs());

            for (var i = 0; i < paramNames.Length; i++)
                if (paramNames[i] != null)
                    return false;
            return true;
        }

        /// <summary>Returns true if type is generic.</summary><param name="type">Type to check.</param> <returns>True if type generic.</returns>
        public static bool IsGeneric(this Type type)
        {
            return type.GetTypeInfo().IsGenericType;
        }

        /// <summary>Returns true if type if generic type definition (open type).</summary><param name="type">Type to check.</param>
        /// <returns>True if type is open type: generic type definition.</returns>
        public static bool IsGenericDefinition(this Type type)
        {
            return type.GetTypeInfo().IsGenericTypeDefinition;
        }

        /// <summary>Returns true if type is closed generic: does not have open generic parameters, only closed/concrete ones.</summary>
        /// <param name="type">Type to check</param> <returns>True if closed generic.</returns>
        public static bool IsClosedGeneric(this Type type)
        {
            return type.GetTypeInfo().IsGenericType && !type.GetTypeInfo().ContainsGenericParameters;
        }

        /// <summary>Returns true if type if open generic: contains at list one open generic parameter. Could be
        /// generic type definition as well.</summary>
        /// <param name="type">Type to check.</param> <returns>True if open generic.</returns>
        public static bool IsOpenGeneric(this Type type)
        {
            return type.GetTypeInfo().IsGenericType && type.GetTypeInfo().ContainsGenericParameters;
        }

        /// <summary>Returns generic type definition if type is generic and null otherwise.</summary>
        /// <param name="type">Source type, could be null.</param> <returns>Generic type definition.</returns>
        public static Type GetGenericDefinitionOrNull(this Type type)
        {
            return type != null && type.GetTypeInfo().IsGenericType ? type.GetGenericTypeDefinition() : null;
        }

        /// <summary>Return generic type parameters and arguments in order they specified. If type is not generic, returns empty array.</summary>
        /// <param name="type">Source type.</param> <returns>Array of generic type arguments (closed/concrete types) and parameters (open).</returns>
        public static Type[] GetGenericParamsAndArgs(this Type type)
        {
            return Portable.GetGenericArguments(type);
        }

        /// <summary>If type is array returns is element type, otherwise returns null.</summary>
        /// <param name="type">Source type.</param> <returns>Array element type or null.</returns>
        public static Type GetElementTypeOrNull(this Type type)
        {
            var typeInfo = type.GetTypeInfo();
            return typeInfo.IsArray ? typeInfo.GetElementType() : null;
        }

        /// <summary>Return base type or null, if not exist (the case for only for object type).</summary> 
        /// <param name="type">Source type.</param> <returns>Base type or null for object.</returns>
        public static Type GetBaseType(this Type type)
        {
            return type.GetTypeInfo().BaseType;
        }

        /// <summary>Checks if type is public or nested public in public type.</summary>
        /// <param name="type">Type to check.</param> <returns>Return true if check succeeded.</returns>
        public static bool IsPublicOrNestedPublic(this Type type)
        {
            var typeInfo = type.GetTypeInfo();
            return typeInfo.IsPublic || typeInfo.IsNestedPublic && typeInfo.DeclaringType.IsPublicOrNestedPublic();
        }

        /// <summary>Returns true if type is value type.</summary>
        /// <param name="type">Type to check.</param> <returns>Check result.</returns>
        public static bool IsValueType(this Type type)
        {
            return type.GetTypeInfo().IsValueType;
        }

        /// <summary>Returns true if type if abstract or interface.</summary>
        /// <param name="type">Type to check.</param> <returns>Check result.</returns>
        public static bool IsAbstract(this Type type)
        {
            return type.GetTypeInfo().IsAbstract;
        }

        /// <summary>Returns true if type is enum type.</summary>
        /// <param name="type">Type to check.</param> <returns>Check result.</returns>
        public static bool IsEnum(this Type type)
        {
            return type.GetTypeInfo().IsEnum;
        }

        /// <summary>Returns true if instance of type is assignable to instance of <paramref name="other"/> type.</summary>
        /// <param name="type">Type to check, could be null.</param> 
        /// <param name="other">Other type to check, could be null.</param>
        /// <returns>Check result.</returns>
        public static bool IsAssignableTo(this Type type, Type other)
        {
            return type != null && other != null && other.GetTypeInfo().IsAssignableFrom(type.GetTypeInfo());
        }

        /// <summary>Returns true if type of <paramref name="obj"/> is assignable to source <paramref name="type"/>.</summary>
        /// <param name="type">Is type of object.</param> <param name="obj">Object to check.</param>
        /// <returns>Check result.</returns>
        public static bool IsTypeOf(this Type type, object obj)
        {
            return obj != null && obj.GetType().IsAssignableTo(type);
        }

        /// <summary>Returns true if provided type IsPitmitive in .Net terms, or enum, or string
        /// , or array of primitives if <paramref name="orArrayOfPrimitives"/> is true.</summary>
        /// <param name="type">Type to check.</param> 
        /// <param name="orArrayOfPrimitives">Says to return true for array or primitives recursively.</param>
        /// <returns>Check result.</returns>
        public static bool IsPrimitive(this Type type, bool orArrayOfPrimitives = false)
        {
            var typeInfo = type.GetTypeInfo();
            return typeInfo.IsPrimitive || typeInfo.IsEnum || type == typeof(string)
                || orArrayOfPrimitives && typeInfo.IsArray && typeInfo.GetElementType().IsPrimitive(true);
        }

        /// <summary>Returns all attributes defined on <param name="type"></param>.</summary>
        /// <param name="type">Type to get attributes for.</param>
        /// <param name="attributeType">(optional) Check only for that attribute type, otherwise for any attribute.</param>
        /// <param name="inherit">(optional) Additionally check for attributes inherited from base type.</param>
        /// <returns>Sequence of found attributes or empty.</returns>
        public static Attribute[] GetAttributes(this Type type, Type attributeType = null, bool inherit = false)
        {
            return type.GetTypeInfo().GetCustomAttributes(attributeType ?? typeof(Attribute), inherit)
                .Cast<Attribute>() // required by .net 4.5
                .ToArrayOrSelf();
        }

        /// <summary>Recursive method to enumerate all input type and its base types for specific details.
        /// Details are returned by <paramref name="getDeclared"/> delegate.</summary>
        /// <typeparam name="T">Details type: properties, fields, methods, etc.</typeparam>
        /// <param name="type">Input type.</param> <param name="getDeclared">Get declared type details.</param>
        /// <returns>Enumerated details info objects.</returns>
        public static IEnumerable<T> GetAll<T>(this Type type, Func<TypeInfo, IEnumerable<T>> getDeclared)
        {
            var typeInfo = type.GetTypeInfo();
            var declared = getDeclared(typeInfo);
            var baseType = typeInfo.BaseType;
            return baseType == null || baseType == typeof(object) ? declared
                : declared.Concat(baseType.GetAll(getDeclared));
        }

        /// <summary>Enumerates all constructors from input type.</summary>
        /// <param name="type">Input type.</param>
        /// <param name="includeNonPublic">(optional) If set include non-public constructors into result.</param>
        /// <returns>Enumerated constructors.</returns>
        public static IEnumerable<ConstructorInfo> GetAllConstructors(this Type type, bool includeNonPublic = false)
        {
            var all = type.GetTypeInfo().DeclaredConstructors;
            return includeNonPublic ? all : all.Where(c => c.IsPublic);
        }

        /// <summary>Searches and returns constructor by its signature.</summary>
        /// <param name="type">Input type.</param>
        /// <param name="includeNonPublic">(optional) If set include non-public constructors into result.</param>
        /// <param name="args">Signature - constructor argument types.</param>
        /// <returns>Found constructor or null.</returns>
        public static ConstructorInfo GetConstructorOrNull(this Type type, bool includeNonPublic = false, params Type[] args)
        {
            return type.GetAllConstructors(includeNonPublic)
                .FirstOrDefault(c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual(args));
        }

        /// <summary>Returns single constructor, otherwise if no or more than one: returns false.</summary>
        /// <param name="type">Type to inspect.</param>
        /// <param name="includeNonPublic">If set, counts non-public constructors.</param>
        /// <returns>Single constructor or null.</returns>
        public static ConstructorInfo GetSingleConstructorOrNull(this Type type, bool includeNonPublic = false)
        {
            var ctors = type.GetAllConstructors(includeNonPublic).ToArrayOrSelf();
            return ctors.Length == 1 ? ctors[0] : null;
        }

        /// <summary>Returns single declared (not inherited) method by name, or null if not found.</summary>
        /// <param name="type">Input type</param> <param name="name">Method name to look for.</param>
        /// <returns>Found method or null.</returns>
        public static MethodInfo GetSingleDeclaredMethodOrNull(this Type type, string name)
        {
            var methods = type.GetTypeInfo().DeclaredMethods.Where(m => m.Name == name).ToArrayOrSelf();
            return methods.Length == 1 ? methods[0] : null;
        }

        /// <summary>Returns declared (not inherited) method by name and argument types, or null if not found.</summary>
        /// <param name="type">Input type</param> <param name="name">Method name to look for.</param>
        /// <param name="args">Argument types</param> <returns>Found method or null.</returns>
        public static MethodInfo GetDeclaredMethodOrNull(this Type type, string name, params Type[] args)
        {
            return type.GetTypeInfo().DeclaredMethods.FirstOrDefault(m =>
                m.Name == name && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(args));
        }

        /// <summary>Returns property by name, including inherited. Or null if not found.</summary>
        /// <param name="type">Input type.</param> <param name="name">Property name to look for.</param>
        /// <returns>Found property or null.</returns>
        public static PropertyInfo GetPropertyOrNull(this Type type, string name)
        {
            return type.GetAll(_ => _.DeclaredProperties).FirstOrDefault(p => p.Name == name);
        }

        /// <summary>Returns field by name, including inherited. Or null if not found.</summary>
        /// <param name="type">Input type.</param> <param name="name">Field name to look for.</param>
        /// <returns>Found field or null.</returns>
        public static FieldInfo GetFieldOrNull(this Type type, string name)
        {
            return type.GetAll(_ => _.DeclaredFields).FirstOrDefault(p => p.Name == name);
        }

        /// <summary>Returns type assembly.</summary> <param name="type">Input type</param> <returns>Type assembly.</returns>
        public static Assembly GetAssembly(this Type type) { return type.GetTypeInfo().Assembly; }

        #region Implementation

        private static void SetNamesFoundInGenericParametersToNull(string[] names, Type[] genericParameters)
        {
            for (var i = 0; i < genericParameters.Length; i++)
            {
                var sourceTypeArg = genericParameters[i];
                if (sourceTypeArg.IsGenericParameter)
                {
                    var matchingTargetArgIndex = Array.IndexOf(names, sourceTypeArg.Name);
                    if (matchingTargetArgIndex != -1)
                        names[matchingTargetArgIndex] = null;
                }
                else if (sourceTypeArg.IsOpenGeneric())
                    SetNamesFoundInGenericParametersToNull(names, sourceTypeArg.GetGenericParamsAndArgs());
            }
        }

        #endregion
    }

    /// <summary>Methods to work with immutable arrays, and general array sugar.</summary>
    public static class ArrayTools
    {
        /// <summary>Returns true if array is null or have no items.</summary> <typeparam name="T">Type of array item.</typeparam>
        /// <param name="source">Source array to check.</param> <returns>True if null or has no items, false otherwise.</returns>
        public static bool IsNullOrEmpty<T>(this T[] source)
        {
            return source == null || source.Length == 0;
        }

        /// <summary>Returns source enumerable if it is array, otherwise converts source to array.</summary>
        /// <typeparam name="T">Array item type.</typeparam>
        /// <param name="source">Source enumerable.</param>
        /// <returns>Source enumerable or its array copy.</returns>
        public static T[] ToArrayOrSelf<T>(this IEnumerable<T> source)
        {
            return source is T[] ? (T[])source : source.ToArray();
        }

        /// <summary>Returns new array consisting from all items from source array then all items from added array.
        /// If source is null or empty, then added array will be returned.
        /// If added is null or empty, then source will be returned.</summary>
        /// <typeparam name="T">Array item type.</typeparam>
        /// <param name="source">Array with leading items.</param>
        /// <param name="added">Array with following items.</param>
        /// <returns>New array with items of source and added arrays.</returns>
        public static T[] Append<T>(this T[] source, params T[] added)
        {
            if (added == null || added.Length == 0)
                return source;
            if (source == null || source.Length == 0)
                return added;
            var result = new T[source.Length + added.Length];
            Array.Copy(source, 0, result, 0, source.Length);
            if (added.Length == 1)
                result[source.Length] = added[0];
            else
                Array.Copy(added, 0, result, source.Length, added.Length);
            return result;
        }

        /// <summary>Returns new array with <paramref name="value"/> appended, 
        /// or <paramref name="value"/> at <paramref name="index"/>, if specified.
        /// If source array could be null or empty, then single value item array will be created despite any index.</summary>
        /// <typeparam name="T">Array item type.</typeparam>
        /// <param name="source">Array to append value to.</param>
        /// <param name="value">Value to append.</param>
        /// <param name="index">(optional) Index of value to update.</param>
        /// <returns>New array with appended or updated value.</returns>
        public static T[] AppendOrUpdate<T>(this T[] source, T value, int index = -1)
        {
            if (source == null || source.Length == 0)
                return new[] { value };
            var sourceLength = source.Length;
            index = index < 0 ? sourceLength : index;
            var result = new T[index < sourceLength ? sourceLength : sourceLength + 1];
            Array.Copy(source, result, sourceLength);
            result[index] = value;
            return result;
        }

        /// <summary>Calls predicate on each item in <paramref name="source"/> array until predicate returns true,
        /// then method will return this item index, or if predicate returns false for each item, method will return -1.</summary>
        /// <typeparam name="T">Type of array items.</typeparam>
        /// <param name="source">Source array to operate: if null or empty, then method will return -1.</param>
        /// <param name="predicate">Delegate to evaluate on each array item until delegate returns true.</param>
        /// <returns>Index of item for which predicate returns true, or -1 otherwise.</returns>
        public static int IndexOf<T>(this T[] source, Func<T, bool> predicate)
        {
            if (source == null || source.Length == 0)
                return -1;
            for (var i = 0; i < source.Length; ++i)
                if (predicate(source[i]))
                    return i;
            return -1;
        }

        /// <summary>Produces new array without item at specified <paramref name="index"/>. 
        /// Will return <paramref name="source"/> array if index is out of bounds, or source is null/empty.</summary>
        /// <typeparam name="T">Type of array item.</typeparam>
        /// <param name="source">Input array.</param> <param name="index">Index if item to remove.</param>
        /// <returns>New array with removed item at index, or input source array if index is not in array.</returns>
        public static T[] RemoveAt<T>(this T[] source, int index)
        {
            if (source == null || source.Length == 0 || index < 0 || index >= source.Length)
                return source;
            if (index == 0 && source.Length == 1)
                return new T[0];
            var result = new T[source.Length - 1];
            if (index != 0)
                Array.Copy(source, 0, result, 0, index);
            if (index != result.Length)
                Array.Copy(source, index + 1, result, index, result.Length - index);
            return result;
        }

        /// <summary>Looks for item in array using equality comparison, and returns new array with found item remove, or original array if not item found.</summary>
        /// <typeparam name="T">Type of array item.</typeparam>
        /// <param name="source">Input array.</param> <param name="value">Value to find and remove.</param>
        /// <returns>New array with value removed or original array if value is not found.</returns>
        public static T[] Remove<T>(this T[] source, T value)
        {
            return source.RemoveAt(source.IndexOf(x => Equals(x, value)));
        }
    }

    /// <summary>Provides pretty printing/debug view for number of types.</summary>
    public static class PrintTools
    {
        /// <summary>Default separator used for printing enumerable.</summary>
        public readonly static string DEFAULT_ITEM_SEPARATOR = ";" + Environment.NewLine;

        /// <summary>Prints input object by using corresponding Print methods for know types.</summary>
        /// <param name="s">Builder to append output to.</param>
        /// <param name="x">Object to print.</param>
        /// <param name="quote">(optional) Quote to use for quoting string object.</param>
        /// <param name="itemSeparator">(optional) Separator for enumerable.</param>
        /// <param name="getTypeName">(optional) Custom type printing policy.</param>
        /// <returns>String builder with appended output.</returns>
        public static StringBuilder Print(this StringBuilder s, object x,
            string quote = null, string itemSeparator = null, Func<Type, string> getTypeName = null)
        {
            return x == null ? s.Append("null")
                : x is string ? s.Print((string)x, quote)
                : x is Type ? s.Print((Type)x, getTypeName)
                : x is IEnumerable<Type> || x is IEnumerable
                    ? s.Print((IEnumerable)x, itemSeparator ?? DEFAULT_ITEM_SEPARATOR, (_, o) => _.Print(o, quote, null, getTypeName))
                : s.Append(x);
        }

        /// <summary>Appends string to string builder quoting with <paramref name="quote"/> if provided.</summary>
        /// <param name="s">String builder to append string to.</param>
        /// <param name="str">String to print.</param>
        /// <param name="quote">(optional) Quote to add before and after string.</param>
        /// <returns>String builder with appended string.</returns>
        public static StringBuilder Print(this StringBuilder s, string str, string quote = null)
        {
            return quote == null ? s.Append(str) : s.Append(quote).Append(str).Append(quote);
        }

        /// <summary>Prints enumerable by using corresponding Print method for known item type.</summary>
        /// <param name="s">String builder to append output to.</param>
        /// <param name="items">Items to print.</param>
        /// <param name="separator">(optional) Custom separator if provided.</param>
        /// <param name="printItem">(optional) Custom item printer if provided.</param>
        /// <returns>String builder with appended output.</returns>
        public static StringBuilder Print(this StringBuilder s, IEnumerable items,
            string separator = ", ", Action<StringBuilder, object> printItem = null)
        {
            if (items == null) return s;
            printItem = printItem ?? ((_, x) => _.Print(x));
            var itemCount = 0;
            foreach (var item in items)
                printItem(itemCount++ == 0 ? s : s.Append(separator), item);
            return s;
        }

        /// <summary>Default delegate to print Type details: by default print <see cref="Type.FullName"/> and
        /// spare namespace if it start with "System."</summary>
        public static readonly Func<Type, string> GetTypeNameDefault = t =>
            t.FullName != null && t.Namespace != null && !t.Namespace.StartsWith("System") ? t.FullName : t.Name;

        /// <summary>Appends <see cref="Type"/> object details to string builder.</summary>
        /// <param name="s">String builder to append output to.</param>
        /// <param name="type">Input type to print.</param>
        /// <param name="getTypeName">(optional) Delegate to provide custom type details.</param>
        /// <returns>String builder with appended output.</returns>
        public static StringBuilder Print(this StringBuilder s, Type type, Func<Type, string> getTypeName = null)
        {
            if (type == null) return s;

            getTypeName = getTypeName ?? GetTypeNameDefault;
            var typeName = getTypeName(type);

            var isArray = type.IsArray;
            if (isArray)
                type = type.GetElementType();

            if (!type.IsGeneric())
                return s.Append(typeName.Replace('+', '.'));

            s.Append(typeName.Substring(0, typeName.IndexOf('`')).Replace('+', '.')).Append('<');

            var genericArgs = type.GetGenericParamsAndArgs();
            if (type.IsGenericDefinition())
                s.Append(',', genericArgs.Length - 1);
            else
                s.Print(genericArgs, ", ", (_, t) => _.Print((Type)t, getTypeName));

            s.Append('>');

            if (isArray)
                s.Append("[]");

            return s;
        }
    }

    /// <summary>Ports some methods from .Net 4.0/4.5</summary>
    public static partial class Portable
    {
        /// <summary>Portable version of Assembly.GetTypes.</summary>
        public static readonly Func<Assembly, IEnumerable<Type>> GetTypesFromAssembly =
            ExpressionTools.GetMethodDelegateOrNull<Assembly, IEnumerable<Type>>("GetTypes").ThrowIfNull();

        /// <summary>Portable version of PropertyInfo.GetSetMethod.</summary>
        public static readonly Func<PropertyInfo, MethodInfo> GetPropertySetMethod =
            ExpressionTools.GetMethodDelegateOrNull<PropertyInfo, MethodInfo>("GetSetMethod").ThrowIfNull();

        /// <summary>Portable version of Type.GetGenericArguments.</summary>
        public static readonly Func<Type, Type[]> GetGenericArguments =
            ExpressionTools.GetMethodDelegateOrNull<Type, Type[]>("GetGenericArguments").ThrowIfNull();

        /// <summary>Returns managed Thread ID either from Environment or Thread.CurrentThread whichever is available.</summary>
        /// <returns>Managed Thread ID.</returns>
        public static int GetCurrentManagedThreadID()
        {
            var resultID = -1;
            GetCurrentManagedThreadID(ref resultID);
            if (resultID == -1) 
                resultID = _getEnvCurrentManagedThreadId();
            return resultID;
        }

        static partial void GetCurrentManagedThreadID(ref int threadID);

        private static readonly MethodInfo _getEnvCurrentManagedThreadIdMethod =
            typeof(Environment).GetDeclaredMethodOrNull("get_CurrentManagedThreadId");

        private static readonly Func<int> _getEnvCurrentManagedThreadId = _getEnvCurrentManagedThreadIdMethod == null ? null
            : Expression.Lambda<Func<int>>(Expression.Call(_getEnvCurrentManagedThreadIdMethod), null).Compile();
    }

    /// <summary>Tools for expressions, that are not supported out-of-box.</summary>
    public static class ExpressionTools
    {
        /// <summary>Extracts method info from method call expression.
        /// It is allow to use type-safe method declaration instead of string method name.</summary>
        /// <param name="methodCall">Lambda wrapping method call.</param>
        /// <returns>Found method info or null if lambda body is not method call.</returns>
        public static MethodInfo GetCalledMethodOrNull(LambdaExpression methodCall)
        {
            var callExpr = methodCall.Body as MethodCallExpression;
            return callExpr == null ? null : callExpr.Method;
        }


        /// <summary>Extracts member info from property or field getter. Enables type-safe property declarations without using strings.</summary>
        /// <typeparam name="T">Type of member holder.</typeparam>
        /// <param name="getter">Expected to contain member access: t => t.MyProperty.</param>
        /// <returns>Extracted member info or null if getter does not contain member access.</returns>
        public static MemberInfo GetAccessedMemberOrNull<T>(Expression<Func<T, object>> getter)
        {
            var body = getter.Body;
            var member = body as MemberExpression ?? ((UnaryExpression)body).Operand as MemberExpression;
            return member == null ? null : member.Member;
        }

        /// <summary>Creates and returns delegate calling method without parameters.</summary>
        /// <typeparam name="TOwner">Method owner type.</typeparam>
        /// <typeparam name="TReturn">Method return type.</typeparam>
        /// <param name="methodName">Method name to find.</param>
        /// <returns>Created delegate or null, if no method with such name is found.</returns>
        public static Func<TOwner, TReturn> GetMethodDelegateOrNull<TOwner, TReturn>(string methodName)
        {
            var methodInfo = typeof(TOwner).GetDeclaredMethodOrNull(methodName);
            if (methodInfo == null) return null;
            var thisExpr = Expression.Parameter(typeof(TOwner), "_");
            var methodExpr = Expression.Lambda<Func<TOwner, TReturn>>(Expression.Call(thisExpr, methodInfo), thisExpr);
            return methodExpr.Compile();
        }

        /// <summary>Creates default(T) expression for provided <paramref name="type"/>.</summary>
        /// <param name="type">Type to get default value of.</param>
        /// <returns>Default value expression.</returns>
        public static Expression GetDefaultValueExpression(this Type type)
        {
            return Expression.Call(_getDefaultMethod.MakeGenericMethod(type), (Expression[])null);
        }

        private static readonly MethodInfo _getDefaultMethod = typeof(ExpressionTools).GetDeclaredMethodOrNull("GetDefault");
        internal static T GetDefault<T>() { return default(T); }
    }

    /// <summary>Immutable Key-Value. It is reference type (could be check for null), 
    /// which is different from System value type <see cref="KeyValuePair{TKey,TValue}"/>.
    /// In addition provides <see cref="Equals"/> and <see cref="GetHashCode"/> implementations.</summary>
    /// <typeparam name="K">Type of Key.</typeparam><typeparam name="V">Type of Value.</typeparam>
    public sealed class KV<K, V>
    {
        /// <summary>Key.</summary>
        public readonly K Key;

        /// <summary>Value.</summary>
        public readonly V Value;

        /// <summary>Creates Key-Value object by providing key and value. Does Not check either one for null.</summary>
        /// <param name="key">key.</param><param name="value">value.</param>
        public KV(K key, V value)
        {
            Key = key;
            Value = value;
        }

        /// <summary>Creates nice string view.</summary><returns>String representation.</returns>
        public override string ToString()
        {
            return new StringBuilder("[").Print(Key).Append(", ").Print(Value).Append("]").ToString();
        }

        /// <summary>Returns true if both key and value are equal to corresponding key-value of other object.</summary>
        /// <param name="obj">Object to check equality with.</param> <returns>True if equal.</returns>
        public override bool Equals(object obj)
        {
            var other = obj as KV<K, V>;
            return other != null
                && (ReferenceEquals(other.Key, Key) || Equals(other.Key, Key))
                && (ReferenceEquals(other.Value, Value) || Equals(other.Value, Value));
        }

        /// <summary>Combines key and value hash code. R# generated default implementation.</summary>
        /// <returns>Combined hash code for key-value.</returns>
        public override int GetHashCode()
        {
            unchecked
            {
                return ((object)Key == null ? 0 : Key.GetHashCode() * 397)
                     ^ ((object)Value == null ? 0 : Value.GetHashCode());
            }
        }
    }

    /// <summary>Delegate for changing value from old one to some new based on provided new value.</summary>
    /// <typeparam name="V">Type of values.</typeparam>
    /// <param name="oldValue">Existing value.</param>
    /// <param name="newValue">New value passed to Update.. method.</param>
    /// <returns>Changed value.</returns>
    public delegate V Update<V>(V oldValue, V newValue);

    public sealed class IntKeyTree
    {
        /// <summary>Empty tree to start with. The <see cref="Height"/> of the empty tree is 0.</summary>
        public static readonly IntKeyTree Empty = new IntKeyTree();

        /// <summary>Key.</summary>
        public readonly int Key;

        /// <summary>Value.</summary>
        public readonly object Value;

        /// <summary>Left subtree/branch, or empty.</summary>
        public readonly IntKeyTree Left;

        /// <summary>Right subtree/branch, or empty.</summary>
        public readonly IntKeyTree Right;

        /// <summary>Height of longest subtree/branch. It is 0 for empty tree, and 1 for single node tree.</summary>
        public readonly int Height;

        /// <summary>Returns true is tree is empty.</summary>
        public bool IsEmpty { get { return Height == 0; } }

        public IntKeyTree AddOrUpdate(int key, object value)
        {
            return AddOrUpdate(key, value, false);
        }

        public IntKeyTree Update(int key, object value)
        {
            return AddOrUpdate(key, value, true);
        }

        public object GetValueOrDefault(int key)
        {
            var t = this;
            while (t.Height != 0 && t.Key != key)
                t = key < t.Key ? t.Left : t.Right;
            return t.Height != 0 ? t.Value : null;
        }

        public IEnumerable<IntKeyTree> Enumerate()
        {
            if (Height == 0)
                yield break;

            var parents = new IntKeyTree[Height];

            var tree = this;
            var parentCount = -1;
            while (tree.Height != 0 || parentCount != -1)
            {
                if (tree.Height != 0)
                {
                    parents[++parentCount] = tree;
                    tree = tree.Left;
                }
                else
                {
                    tree = parents[parentCount--];
                    yield return tree;
                    tree = tree.Right;
                }
            }
        }

        #region Implementation

        private IntKeyTree() { }

        private IntKeyTree(int key, object value, IntKeyTree left, IntKeyTree right)
        {
            Key = key;
            Value = value;
            Left = left;
            Right = right;
            Height = 1 + (left.Height > right.Height ? left.Height : right.Height);
        }

        private IntKeyTree AddOrUpdate(int key, object value, bool updateOnly)
        {
            return Height == 0 ? (updateOnly ? this : new IntKeyTree(key, value, Empty, Empty)) // if not found and updateOnly returning current tree.
                : (key == Key ? new IntKeyTree(key, value, Left, Right) // actual update
                : (key < Key
                    ? With(Left.AddOrUpdate(key, value, updateOnly), Right)
                    : With(Left, Right.AddOrUpdate(key, value, updateOnly))).KeepBalanced());
        }

        private IntKeyTree KeepBalanced()
        {
            var delta = Left.Height - Right.Height;
            return delta >= 2 ? With(Left.Right.Height - Left.Left.Height == 1 ? Left.RotateLeft() : Left, Right).RotateRight()
                : (delta <= -2 ? With(Left, Right.Left.Height - Right.Right.Height == 1 ? Right.RotateRight() : Right).RotateLeft()
                : this);
        }

        private IntKeyTree RotateRight()
        {
            return Left.With(Left.Left, With(Left.Right, Right));
        }

        private IntKeyTree RotateLeft()
        {
            return Right.With(With(Left, Right.Left), Right.Right);
        }

        private IntKeyTree With(IntKeyTree left, IntKeyTree right)
        {
            return left == Left && right == Right ? this : new IntKeyTree(Key, Value, left, right);
        }

        #endregion
    }

    /// <summary>Immutable http://en.wikipedia.org/wiki/AVL_tree where actual node key is hash code of <typeparamref name="K"/>.</summary>
    public sealed class HashTree<K, V>
    {
        /// <summary>Empty tree to start with. The <see cref="Height"/> of the empty tree is 0.</summary>
        public static readonly HashTree<K, V> Empty = new HashTree<K, V>();

        /// <summary>Key of type K that should support <see cref="object.Equals(object)"/> and <see cref="object.GetHashCode"/>.</summary>
        public readonly K Key;

        /// <summary>Value of any type V.</summary>
        public readonly V Value;

        /// <summary>Hash calculated from <see cref="Key"/> with <see cref="object.GetHashCode"/>. Hash is stored to improve speed.</summary>
        public readonly int Hash;

        /// <summary>In case of <see cref="Hash"/> conflicts for different keys contains conflicted keys with their values.</summary>
        public readonly KV<K, V>[] Conflicts;

        /// <summary>Left subtree/branch, or empty.</summary>
        public readonly HashTree<K, V> Left;

        /// <summary>Right subtree/branch, or empty.</summary>
        public readonly HashTree<K, V> Right;

        /// <summary>Height of longest subtree/branch. It is 0 for empty tree, and 1 for single node tree.</summary>
        public readonly int Height;

        /// <summary>Returns true is tree is empty.</summary>
        public bool IsEmpty { get { return Height == 0; } }

        /// <summary>Returns new tree with added key-value. If value with the same key is exist, then
        /// if <paramref name="update"/> is not specified: then existing value will be replaced by <paramref name="value"/>;
        /// if <paramref name="update"/> is specified: then update delegate will decide what value to keep.</summary>
        /// <param name="key">Key to add.</param><param name="value">Value to add.</param>
        /// <param name="update">(optional) Delegate to decide what value to keep: old or new one.</param>
        /// <returns>New tree with added or updated key-value.</returns>
        public HashTree<K, V> AddOrUpdate(K key, V value, Update<V> update = null)
        {
            return AddOrUpdate(key.GetHashCode(), key, value, update, updateOnly: false);
        }

        /// <summary>Looks for <paramref name="key"/> and replaces its value with new <paramref name="value"/>, or 
        /// it may use <paramref name="update"/> for more complex update logic. Returns new tree with updated value,
        /// or the SAME tree if key is not found.</summary>
        /// <param name="key">Key to look for.</param>
        /// <param name="value">New value to replace key value with.</param>
        /// <param name="update">(optional) Delegate for custom update logic, it gets old and new <paramref name="value"/>
        /// as inputs and should return updated value as output.</param>
        /// <returns>New tree with updated value or the SAME tree if no key found.</returns>
        public HashTree<K, V> Update(K key, V value, Update<V> update = null)
        {
            return AddOrUpdate(key.GetHashCode(), key, value, update, updateOnly: true);
        }

        /// <summary>Searches for key in tree and returns the value if found, or <paramref name="defaultValue"/> otherwise.</summary>
        /// <param name="key">Key to look for.</param> <param name="defaultValue">Value to return if key is not found.</param>
        /// <returns>Found value or <paramref name="defaultValue"/>.</returns>
        public V GetValueOrDefault(K key, V defaultValue = default(V))
        {
            var t = this;
            var hash = key.GetHashCode();
            while (t.Height != 0 && t.Hash != hash)
                t = hash < t.Hash ? t.Left : t.Right;
            return t.Height != 0 && (ReferenceEquals(key, t.Key) || key.Equals(t.Key))
                ? t.Value : t.GetConflictedValueOrDefault(key, defaultValue);
        }

        /// <summary>Depth-first in-order traversal as described in http://en.wikipedia.org/wiki/Tree_traversal
        /// The only difference is using fixed size array instead of stack for speed-up (~20% faster than stack).</summary>
        /// <returns>Sequence of enumerated key value pairs.</returns>
        public IEnumerable<KV<K, V>> Enumerate()
        {
            if (Height == 0)
                yield break;

            var parents = new HashTree<K, V>[Height];

            var tree = this;
            var parentCount = -1;
            while (tree.Height != 0 || parentCount != -1)
            {
                if (tree.Height != 0)
                {
                    parents[++parentCount] = tree;
                    tree = tree.Left;
                }
                else
                {
                    tree = parents[parentCount--];
                    yield return new KV<K, V>(tree.Key, tree.Value);

                    if (tree.Conflicts != null)
                        for (var i = 0; i < tree.Conflicts.Length; i++)
                            yield return tree.Conflicts[i];

                    tree = tree.Right;
                }
            }
        }

        #region Implementation

        private HashTree() { }

        private HashTree(int hash, K key, V value, KV<K, V>[] conficts, HashTree<K, V> left, HashTree<K, V> right)
        {
            Hash = hash;
            Key = key;
            Value = value;
            Conflicts = conficts;
            Left = left;
            Right = right;
            Height = 1 + (left.Height > right.Height ? left.Height : right.Height);
        }

        private HashTree<K, V> AddOrUpdate(int hash, K key, V value, Update<V> update, bool updateOnly)
        {
            return Height == 0 ? (updateOnly ? this : new HashTree<K, V>(hash, key, value, null, Empty, Empty))
                : (hash == Hash ? UpdateValueAndResolveConflicts(key, value, update, updateOnly)
                : (hash < Hash
                    ? With(Left.AddOrUpdate(hash, key, value, update, updateOnly), Right)
                    : With(Left, Right.AddOrUpdate(hash, key, value, update, updateOnly))).KeepBalanced());
        }

        private HashTree<K, V> UpdateValueAndResolveConflicts(K key, V value, Update<V> update, bool updateOnly)
        {
            if (ReferenceEquals(Key, key) || Key.Equals(key))
                return new HashTree<K, V>(Hash, key, update == null ? value : update(Value, value), Conflicts, Left, Right);

            if (Conflicts == null) // add only if updateOnly is false.
                return updateOnly ? this
                    : new HashTree<K, V>(Hash, Key, Value, new[] { new KV<K, V>(key, value) }, Left, Right);

            var found = Conflicts.Length - 1;
            while (found >= 0 && !Equals(Conflicts[found].Key, Key)) --found;
            if (found == -1)
            {
                if (updateOnly) return this;
                var newConflicts = new KV<K, V>[Conflicts.Length + 1];
                Array.Copy(Conflicts, 0, newConflicts, 0, Conflicts.Length);
                newConflicts[Conflicts.Length] = new KV<K, V>(key, value);
                return new HashTree<K, V>(Hash, Key, Value, newConflicts, Left, Right);
            }

            var conflicts = new KV<K, V>[Conflicts.Length];
            Array.Copy(Conflicts, 0, conflicts, 0, Conflicts.Length);
            conflicts[found] = new KV<K, V>(key, update == null ? value : update(Conflicts[found].Value, value));
            return new HashTree<K, V>(Hash, Key, Value, conflicts, Left, Right);
        }

        private V GetConflictedValueOrDefault(K key, V defaultValue)
        {
            if (Conflicts != null)
                for (var i = 0; i < Conflicts.Length; i++)
                    if (Equals(Conflicts[i].Key, key))
                        return Conflicts[i].Value;
            return defaultValue;
        }

        private HashTree<K, V> KeepBalanced()
        {
            var delta = Left.Height - Right.Height;
            return delta >= 2 ? With(Left.Right.Height - Left.Left.Height == 1 ? Left.RotateLeft() : Left, Right).RotateRight()
                : (delta <= -2 ? With(Left, Right.Left.Height - Right.Right.Height == 1 ? Right.RotateRight() : Right).RotateLeft()
                : this);
        }

        private HashTree<K, V> RotateRight()
        {
            return Left.With(Left.Left, With(Left.Right, Right));
        }

        private HashTree<K, V> RotateLeft()
        {
            return Right.With(With(Left, Right.Left), Right.Right);
        }

        private HashTree<K, V> With(HashTree<K, V> left, HashTree<K, V> right)
        {
            return left == Left && right == Right ? this : new HashTree<K, V>(Hash, Key, Value, Conflicts, left, right);
        }

        #endregion
    }

    /// <summary>Provides optimistic-concurrency consistent <see cref="Swap{T}"/> operation.</summary>
    public static class Ref
    {
        /// <summary>Factory for <see cref="Ref{T}"/> with type of value inference.</summary>
        /// <typeparam name="T">Type of value to wrap.</typeparam>
        /// <param name="value">Initial value to wrap.</param>
        /// <returns>New ref.</returns>
        public static Ref<T> Of<T>(T value = default(T)) where T : class
        {
            return new Ref<T>(value);
        }

        /// <summary>First, it evaluates new value using <paramref name="getNewValue"/> function. 
        /// Second, it checks that original value is not changed. 
        /// If it is changed it will retry first step, otherwise it assigns new value and returns original (the one used for <paramref name="getNewValue"/>).</summary>
        /// <typeparam name="T">Type of value to swap.</typeparam>
        /// <param name="value">Reference to change to new value</param>
        /// <param name="getNewValue">Delegate to get value from old one.</param>
        /// <returns>Old/original value. By analogy with <see cref="Interlocked.Exchange(ref int,int)"/>.</returns>
        /// <remarks>Important: <paramref name="getNewValue"/> May be called multiple times to retry update with value concurrently changed by other code.</remarks>
        public static T Swap<T>(ref T value, Func<T, T> getNewValue) where T : class
        {
            var retryCount = 0;
            while (true)
            {
                var oldValue = value;
                var newValue = getNewValue(oldValue);
                if (Interlocked.CompareExchange(ref value, newValue, oldValue) == oldValue)
                    return oldValue;
                if (++retryCount > RETRY_COUNT_UNTIL_THROW)
                    throw new InvalidOperationException(ERROR_RETRY_COUNT_EXCEEDED);
            }
        }

        private const int RETRY_COUNT_UNTIL_THROW = 50;
        private static readonly string ERROR_RETRY_COUNT_EXCEEDED =
            "Ref retried to Update for " + RETRY_COUNT_UNTIL_THROW + " times But there is always someone else intervened.";
    }

    /// <summary>Wrapper that provides optimistic-concurrency Swap operation implemented using <see cref="Ref.Swap{T}"/>.</summary>
    /// <typeparam name="T">Type of object to wrap.</typeparam>
    public sealed class Ref<T> where T : class
    {
        /// <summary>Gets the wrapped value.</summary>
        public T Value { get { return _value; } }

        /// <summary>Creates ref to object, optionally with initial value provided.</summary>
        /// <param name="initialValue">Initial object value.</param>
        public Ref(T initialValue = default(T))
        {
            _value = initialValue;
        }

        /// <summary>Exchanges currently hold object with <paramref name="getNewValue"/> result: see <see cref="Ref.Swap{T}"/> for details.</summary>
        /// <param name="getNewValue">Delegate to produce new object value from current one passed as parameter.</param>
        /// <returns>Returns old object value the same way as <see cref="Interlocked.Exchange(ref int,int)"/></returns>
        /// <remarks>Important: <paramref name="getNewValue"/> May be called multiple times to retry update with value concurrently changed by other code.</remarks>
        public T Swap(Func<T, T> getNewValue)
        {
            return Ref.Swap(ref _value, getNewValue);
        }

        /// <summary>Simplified version of Swap ignoring old value.</summary>
        /// <param name="newValue">New value to set</param> <returns>Old value.</returns>
        public T Swap(T newValue)
        {
            return Interlocked.Exchange(ref _value, newValue);
        }

        private T _value;
    }
}