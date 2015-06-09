using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Core.Metadata.Edm;
using System.Data.Entity.Infrastructure;
using System.Linq;

namespace EfGraph
{
    public static class DbContextExtensions
    {
        /// <summary>
        /// As we perform this lookup millions of times, let's cache it.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, ReadOnlyMetadataCollection<NavigationProperty>> NavigationProperties;

        static DbContextExtensions()
        {
            NavigationProperties = new ConcurrentDictionary<Type, ReadOnlyMetadataCollection<NavigationProperty>>();
        }

        public static T UpdateGraph<T>(this DbContext context, T entity)
        {
            throw new NotImplementedException();
        }

        public static object GetPrimaryKeyFor(this DbContext context, object obj)
        {
            var entityType = obj.GetType();
            var objectContext = ((IObjectContextAdapter)context).ObjectContext;
            var metadata = objectContext.MetadataWorkspace
                .GetItems<EntityType>(DataSpace.OSpace)
                .SingleOrDefault(p => p.FullName == entityType.FullName);

            if (metadata == null)
            {
                throw new InvalidOperationException(String.Format("The type {0} is not known to the DbContext.",
                    entityType.FullName));
            }

            var keyName = metadata.KeyMembers.Select(k => k.Name).Single();

            return entityType.GetProperty(keyName).GetValue(obj);
        }


        public static IEnumerable<object> GetPrimaryKeysFor(this DbContext context, object obj)
        {
            
            var entityType = obj.GetType();
            var objectContext = ((IObjectContextAdapter)context).ObjectContext;
            var metadata = objectContext.MetadataWorkspace
                .GetItems<EntityType>(DataSpace.OSpace)
                .SingleOrDefault(p => p.FullName == entityType.FullName);

            if (metadata == null)
            {
                throw new InvalidOperationException(String.Format("The type {0} is not known to the DbContext.",
                    entityType.FullName));
            }

            var keyNames = metadata.KeyMembers.Select(k => k.Name);

            return keyNames.Select(keyName => entityType.GetProperty(keyName).GetValue(obj)).ToList();
        }

        /// <summary>
        /// Loads the entire object graph for a root domain object
        /// </summary>
        /// <typeparam name="TEntity">The type of the root object</typeparam>
        /// <param name="context">The context from which to load</param>
        /// <param name="entity">The materialized root entity</param>
        /// <returns>A fully hydrated object graph</returns>
        public static TEntity LoadObjectGraphFor<TEntity>(this DbContext context, TEntity entity) where TEntity : class
        {
            return (TEntity)LoadObjectGraphFor(context, (object)entity);
        }

        /// <summary>
        /// Loads the entire object graph for a root domain object
        /// </summary>
        /// <param name="context">The context from which to load</param>
        /// <param name="entity">The materialized root entity</param>
        /// <returns>A fully hydrated object graph</returns>
        public static object LoadObjectGraphFor(this DbContext context, object entity)
        {
            // Remember change tracking setting
            var detectChanges = context.Configuration.AutoDetectChangesEnabled;
            try
            {
                // Disable change tracking while loading - major performance improvement and we don't need tracking.
                context.Configuration.AutoDetectChangesEnabled = false;
                return LoadObjectGraphInternal(context, entity);
            }
            finally
            {
                // Restore change tracking setting
                context.Configuration.AutoDetectChangesEnabled = detectChanges;
            }

        }

        public static List<object> LoadObjectGraphFor(this DbContext context, List<object> entities)
        {
            if (entities != null && entities.Count > 0)
            {
                // Remember change tracking setting
                var detectChanges = context.Configuration.AutoDetectChangesEnabled;
                try
                {
                    // Disable change tracking while loading - major performance improvement and we don't need tracking.
                    context.Configuration.AutoDetectChangesEnabled = false;
                    for (var x = 0; x < entities.Count; x++)
                    {
                        entities[x] = LoadObjectGraphInternal(context, entities[x]);
                    }
                }
                finally
                {
                    // Restore change tracking setting   
                    context.Configuration.AutoDetectChangesEnabled = detectChanges;
                }
            }
            return entities;
        }
        /// <summary>
        /// Loads the entire object graph for a list of root objects
        /// </summary>
        /// <typeparam name="TRoot">The type of the root objects</typeparam>
        /// <param name="context">The context from which to load</param>
        /// <param name="entities">THe materialized root entities themselves</param>
        /// <returns>A collection of fully hydrated object graphs.</returns>
        public static List<TRoot> LoadObjectGraphFor<TRoot>(this DbContext context, List<TRoot> entities)
            where TRoot : class
        {
            if (entities != null && entities.Count > 0)
            {
                // Remember change tracking setting
                var detectChanges = context.Configuration.AutoDetectChangesEnabled;
                try
                {
                    // Disable change tracking while loading - major performance improvement and we don't need tracking.
                    context.Configuration.AutoDetectChangesEnabled = false;
                    for (var x = 0; x < entities.Count; x++)
                    {
                        entities[x] = (TRoot)LoadObjectGraphInternal(context, entities[x]);
                    }
                }
                finally
                {
                    // Restore change tracking setting   
                    context.Configuration.AutoDetectChangesEnabled = detectChanges;
                }
            }
            return entities;
        }

        private static object LoadObjectGraphInternal(DbContext context, object entity)
        {
            if (entity != null)
            {
                var entityType = entity.GetType();
                var navigationProperties = GetNavigationPropertiesForType(context, entityType).ToList();
                var entry = context.Entry(entity);

                // For each navigation property
                foreach (var navigationProperty in navigationProperties)
                {
                    // Get the underlying type of the property.
                    var property = entityType.GetProperty(navigationProperty.Name);

                    // Get its implementation if IEnumerable
                    var enumerableInterfaceType = property.PropertyType.GetInterface(typeof(IEnumerable<>).Name);
                    // If it is enumerable, we'll load the collection
                    if (enumerableInterfaceType != null)
                    {
                        var collection = entry.Collection(navigationProperty.Name);
                        // If this collection isn't already cached in the DbContext, we'll load it
                        if (!collection.IsLoaded)
                        {
                            collection.Load();
                            // Get the actual value of the collection property on the entity
                            var values = property.GetValue(entity) as IEnumerable;
                            if (values != null)
                            {
                                // We'll create a List<T>, where T is the type of the IEnumerable.
                                // Our navigation property is a collection that implements IEnumerable. 
                                // The 0th index generic argument of IEnumerable is the type. ie. List<string>'s generic argument[0] is string
                                var childType = enumerableInterfaceType.GetGenericArguments()[0];
                                var list = CreateGenericListForType(childType);
                                foreach (var value in values)
                                {
                                    // Recursively load the object graph for each item in the list
                                    list.Add(LoadObjectGraphInternal(context, value));
                                }
                            }
                        }
                    }
                    // Otherwise we'll load the referenced entity
                    else
                    {
                        var reference = entry.Reference(navigationProperty.Name);
                        // If this reference isn't already cached in the DbContext, we'll recursively load its object graph
                        if (!reference.IsLoaded)
                        {
                            reference.Load();
                            var childGraph = LoadObjectGraphInternal(context, property.GetValue(entity));
                            property.SetValue(entity, childGraph);
                        }
                    }

                }

            }
            return entity;
        }

        private static IList CreateGenericListForType(Type type)
        {
            var listType = typeof(List<>).MakeGenericType(type);
            return (IList)Activator.CreateInstance(listType);
        }

        /// <summary>
        /// Finds all navigation properties for a given type as defined by a DbContext
        /// </summary>
        /// <param name="context">The context to query</param>
        /// <param name="entityType">The type of the entity</param>
        /// <returns>A collection of navigation properties</returns>
        private static IEnumerable<NavigationProperty> GetNavigationPropertiesForType(IObjectContextAdapter context,
            Type entityType)
        {
            // Do a dictionary lookup, otherwise call into EF to find all the navigation properties for a given type.
            if (!NavigationProperties.ContainsKey(entityType))
            {
                NavigationProperties[entityType] = context.ObjectContext.MetadataWorkspace
                    .GetItems<EntityType>(DataSpace.OSpace)
                    .Single(p => p.FullName == entityType.FullName)
                    .NavigationProperties;

            }
            return NavigationProperties[entityType];
        }

    }
}
