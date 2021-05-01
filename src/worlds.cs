// ----------------------------------------------------------------------------
// The MIT License
// Lightweight ECS framework https://github.com/Leopotam/ecslite
// Copyright (c) 2021 Leopotam <leopotam@gmail.com>
// ----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

#if ENABLE_IL2CPP
using Unity.IL2CPP.CompilerServices;
#endif

namespace Leopotam.EcsLite {
#if ENABLE_IL2CPP
    [Il2CppSetOption (Option.NullChecks, false)]
    [Il2CppSetOption (Option.ArrayBoundsChecks, false)]
#endif
    public sealed class EcsWorld {
        internal struct EntityData {
            public short Gen;
            public short ComponentsCount;
        }

        internal EntityData[] Entities;
        int _entitiesCount;
        int[] _recycledEntities;
        int _recycledEntitiesCount;
        IEcsPool[] _pools;
        int _poolsCount;
        readonly Dictionary<Type, IEcsPool> _poolHashes;
        readonly Dictionary<int, EcsFilter> _filters;
        List<EcsFilter>[] _filtersByIncludedComponents;
        List<EcsFilter>[] _filtersByExcludedComponents;
        bool _destroyed;

        public EcsWorld () {
            Entities = new EntityData[512];
            _entitiesCount = 0;
            _recycledEntities = new int[512];
            _filters = new Dictionary<int, EcsFilter> (512);
            _pools = new IEcsPool[512];
            _poolsCount = 0;
            _poolHashes = new Dictionary<Type, IEcsPool> (512);
            _filtersByIncludedComponents = new List<EcsFilter>[512];
            _filtersByExcludedComponents = new List<EcsFilter>[512];
            _destroyed = false;
        }

        public void Destroy () {
            _destroyed = true;
            // FIXME: remove all entities.
            for (var i = _entitiesCount - 1; i >= 0; i--) {
                ref var entityData = ref Entities[i];
                if (entityData.ComponentsCount > 0) {
                    DelEntity (i);
                }
            }
            _pools = Array.Empty<IEcsPool> ();
            _poolHashes.Clear ();
            _filters.Clear ();
            _filtersByIncludedComponents = Array.Empty<List<EcsFilter>> ();
            _filtersByExcludedComponents = Array.Empty<List<EcsFilter>> ();
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public bool IsAlive () {
            return !_destroyed;
        }

        public int NewEntity () {
            int entity;
            if (_recycledEntitiesCount > 0) {
                entity = _recycledEntities[--_recycledEntitiesCount];
                ref var entityData = ref Entities[entity];
                entityData.Gen = (short) -entityData.Gen;
            } else {
                // new entity.
                if (_entitiesCount == Entities.Length) {
                    // resize entities and component pools.
                    var newSize = _entitiesCount << 1;
                    Array.Resize (ref Entities, newSize);
                    for (int i = 0, iMax = _poolsCount; i < iMax; i++) {
                        _pools[i].Resize (newSize);
                    }
                }
                entity = _entitiesCount++;
                Entities[entity].Gen = 1;
                for (int i = 0, iMax = _poolsCount; i < iMax; i++) {
                    _pools[i].InitAutoReset (entity);
                }
            }
            return entity;
        }

        public void DelEntity (int entity) {
#if DEBUG
            if (entity < 0 || entity >= _entitiesCount) { throw new Exception ("Cant touch destroyed entity."); }
#endif
            ref var entityData = ref Entities[entity];
            if (entityData.Gen < 0) {
                return;
            }
            // kill components.
            if (entityData.ComponentsCount > 0) {
                var idx = 0;
                while (entityData.ComponentsCount > 0 && idx < _poolsCount) {
                    for (; idx < _poolsCount; idx++) {
                        if (_pools[idx].Has (entity)) {
                            _pools[idx++].Del (entity);
                            break;
                        }
                    }
                }
#if DEBUG
                if (entityData.ComponentsCount != 0) { throw new Exception ($"invalid components count on entity {entity} => {entityData.ComponentsCount}"); }
#endif
                return;
            }
            entityData.Gen = (short) (entityData.Gen == short.MaxValue ? -1 : -(entityData.Gen + 1));
            if (_recycledEntitiesCount == _recycledEntities.Length) {
                Array.Resize (ref _recycledEntities, _recycledEntitiesCount << 1);
            }
            _recycledEntities[_recycledEntitiesCount++] = entity;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public int GetComponentsCount (int entity) {
            return Entities[entity].ComponentsCount;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public bool IsEntityAlive (int entity) {
            return entity >= 0 && entity < _entitiesCount && Entities[entity].Gen > 0;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        public short GetEntityGen (int entity) {
            return Entities[entity].Gen;
        }

        public EcsPool<T> GetPool<T> () where T : struct {
            var poolType = typeof (EcsPool<T>);
            if (_poolHashes.TryGetValue (poolType, out var rawPool)) {
                return (EcsPool<T>) rawPool;
            }
            var pool = new EcsPool<T> (this, _poolsCount, Entities.Length);
            _poolHashes[poolType] = pool;
            if (_poolsCount == _pools.Length) {
                var newSize = _poolsCount << 1;
                Array.Resize (ref _pools, newSize);
                Array.Resize (ref _filtersByIncludedComponents, newSize);
                Array.Resize (ref _filtersByExcludedComponents, newSize);
            }
            _pools[_poolsCount++] = pool;
            return pool;
        }

        public int GetAllEntities (ref int[] entities) {
            var count = _entitiesCount - _recycledEntitiesCount;
            if (entities == null || entities.Length < count) {
                entities = new int[count];
            }
            var id = 0;
            for (int i = 0, iMax = _entitiesCount; i < iMax; i++) {
                ref var entityData = ref Entities[i];
                // should we skip empty entities here?
                if (entityData.ComponentsCount >= 0) {
                    entities[id++] = i;
                }
            }
            return count;
        }

        internal (EcsFilter, bool) GetFilter (EcsFilterMask mask, int capacity = 512) {
            var hash = mask.Hash;
            var exists = _filters.TryGetValue (hash, out var filter);
            if (exists) { return (filter, false); }
            filter = new EcsFilter (this, mask, capacity);
            _filters[hash] = filter;
            // add to component dictionaries for fast compatibility scan.
            for (int i = 0, iMax = mask.IncludeCount; i < iMax; i++) {
                var list = _filtersByIncludedComponents[mask.Include[i]];
                if (list == null) {
                    list = new List<EcsFilter> (8);
                    _filtersByIncludedComponents[mask.Include[i]] = list;
                }
                list.Add (filter);
            }
            for (int i = 0, iMax = mask.ExcludeCount; i < iMax; i++) {
                var list = _filtersByExcludedComponents[mask.Exclude[i]];
                if (list == null) {
                    list = new List<EcsFilter> (8);
                    _filtersByExcludedComponents[mask.Exclude[i]] = list;
                }
                list.Add (filter);
            }
            // scan exist entities for compatibility with new filter.
            for (int i = 0, iMax = _entitiesCount; i < iMax; i++) {
                ref var entityData = ref Entities[i];
                if (entityData.ComponentsCount > 0 && IsMaskCompatible (mask, i)) {
                    filter.AddEntity (i);
                }
            }
            return (filter, true);
        }

        internal void OnEntityChange (int entity, int componentType, bool added) {
            var includeList = _filtersByIncludedComponents[componentType];
            var excludeList = _filtersByExcludedComponents[componentType];
            if (added) {
                // add component.
                if (includeList != null) {
                    foreach (var filter in includeList) {
                        if (IsMaskCompatible (filter.GetMask (), entity)) {
#if DEBUG
                            if (filter.EntitiesMap.ContainsKey (entity)) { throw new Exception ("Entity already in filter."); }
#endif
                            filter.AddEntity (entity);
                        }
                    }
                }
                if (excludeList != null) {
                    foreach (var filter in excludeList) {
                        if (IsMaskCompatibleWithout (filter.GetMask (), entity, componentType)) {
#if DEBUG
                            if (!filter.EntitiesMap.ContainsKey (entity)) { throw new Exception ("Entity not in filter."); }
#endif
                            filter.RemoveEntity (entity);
                        }
                    }
                }
            } else {
                // remove component.
                if (includeList != null) {
                    foreach (var filter in includeList) {
                        if (IsMaskCompatible (filter.GetMask (), entity)) {
#if DEBUG
                            if (!filter.EntitiesMap.ContainsKey (entity)) { throw new Exception ("Entity not in filter."); }
#endif
                            filter.RemoveEntity (entity);
                        }
                    }
                }
                if (excludeList != null) {
                    foreach (var filter in excludeList) {
                        if (IsMaskCompatibleWithout (filter.GetMask (), entity, componentType)) {
#if DEBUG
                            if (filter.EntitiesMap.ContainsKey (entity)) { throw new Exception ("Entity already in filter."); }
#endif
                            filter.AddEntity (entity);
                        }
                    }
                }
            }
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        bool IsMaskCompatible (EcsFilterMask filterMask, int entity) {
            for (int i = 0, iMax = filterMask.IncludeCount; i < iMax; i++) {
                if (!_pools[filterMask.Include[i]].Has (entity)) {
                    return false;
                }
            }
            for (int i = 0, iMax = filterMask.ExcludeCount; i < iMax; i++) {
                if (_pools[filterMask.Exclude[i]].Has (entity)) {
                    return false;
                }
            }
            return true;
        }

        [MethodImpl (MethodImplOptions.AggressiveInlining)]
        bool IsMaskCompatibleWithout (EcsFilterMask filterMask, int entity, int componentId) {
            for (int i = 0, iMax = filterMask.IncludeCount; i < iMax; i++) {
                var typeId = filterMask.Include[i];
                if (typeId == componentId || !_pools[typeId].Has (entity)) {
                    return false;
                }
            }
            for (int i = 0, iMax = filterMask.ExcludeCount; i < iMax; i++) {
                var typeId = filterMask.Exclude[i];
                if (typeId != componentId && _pools[typeId].Has (entity)) {
                    return false;
                }
            }
            return true;
        }
    }
}

#if ENABLE_IL2CPP
// Unity IL2CPP performance optimization attribute.
namespace Unity.IL2CPP.CompilerServices {
    enum Option {
        NullChecks = 1,
        ArrayBoundsChecks = 2
    }

    [AttributeUsage (AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
    class Il2CppSetOptionAttribute : Attribute {
        public Option Option { get; private set; }
        public object Value { get; private set; }

        public Il2CppSetOptionAttribute (Option option, object value) { Option = option; Value = value; }
    }
}
#endif