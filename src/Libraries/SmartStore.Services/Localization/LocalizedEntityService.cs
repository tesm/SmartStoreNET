using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using SmartStore.Core;
using SmartStore.Core.Caching;
using SmartStore.Core.Data;
using SmartStore.Core.Domain.Common;
using SmartStore.Core.Domain.Localization;

namespace SmartStore.Services.Localization
{
	/// <summary>
	/// Provides information about localizable entities
	/// </summary>
	public partial class LocalizedEntityService : ScopedServiceBase, ILocalizedEntityService
    {
		/// <summary>
		/// 0 = segment (keygroup.key.idrange), 1 = language id
		/// </summary>
		const string LOCALIZEDPROPERTY_SEGMENT_KEY = "localizedproperty:{0}-lang-{1}";
		const string LOCALIZEDPROPERTY_SEGMENT_PATTERN = "localizedproperty:{0}*";
		const string LOCALIZEDPROPERTY_ALLSEGMENTS_PATTERN = "localizedproperty:*";

		private readonly IRepository<LocalizedProperty> _localizedPropertyRepository;
        private readonly ICacheManager _cacheManager;
		private readonly PerformanceSettings _performanceSettings;

		private readonly IDictionary<string, LocalizedPropertyCollection> _prefetchedCollections;

        public LocalizedEntityService(
			ICacheManager cacheManager, 
			IRepository<LocalizedProperty> localizedPropertyRepository,
			PerformanceSettings performanceSettings)
        {
            _cacheManager = cacheManager;
            _localizedPropertyRepository = localizedPropertyRepository;
			_performanceSettings = performanceSettings;

			_prefetchedCollections = new Dictionary<string, LocalizedPropertyCollection>(StringComparer.OrdinalIgnoreCase);
		}

		protected override void OnClearCache()
		{
			_cacheManager.RemoveByPattern(LOCALIZEDPROPERTY_ALLSEGMENTS_PATTERN);
		}

		protected virtual IDictionary<int, string> GetCacheSegment(string localeKeyGroup, string localeKey, int entityId, int languageId)
		{
			Guard.NotEmpty(localeKeyGroup, nameof(localeKeyGroup));
			Guard.NotEmpty(localeKey, nameof(localeKey));

			var segmentKey = GetSegmentKeyPart(localeKeyGroup, localeKey, entityId, out var minEntityId, out var maxEntityId);
			var cacheKey = BuildCacheSegmentKey(segmentKey, languageId);

			// TODO: (MC) skip caching product.fulldescription (?), OR
			// ...additionally segment by entity id ranges.

			return _cacheManager.Get(cacheKey, () =>
			{
				var properties = _localizedPropertyRepository.TableUntracked
					.Where(x => x.EntityId >= minEntityId && x.EntityId <= maxEntityId && x.LocaleKey == localeKey && x.LocaleKeyGroup == localeKeyGroup && x.LanguageId == languageId)
					.ToList();

				var dict = new Dictionary<int, string>(properties.Count);

				foreach (var prop in properties)
				{
					dict[prop.EntityId] = prop.LocaleValue.EmptyNull();
				}

				return dict;
			});
		}

		/// <summary>
		/// Clears the cached segment from the cache
		/// </summary>
		protected virtual void ClearCacheSegment(string localeKeyGroup, string localeKey, int entityId, int? languageId = null)
		{
			try
			{
				if (IsInScope)
					return;

				var segmentKey = GetSegmentKeyPart(localeKeyGroup, localeKey, entityId);

				if (languageId.HasValue && languageId.Value > 0)
				{
					_cacheManager.Remove(BuildCacheSegmentKey(segmentKey, languageId.Value));
				}
				else
				{
					_cacheManager.RemoveByPattern(LOCALIZEDPROPERTY_SEGMENT_PATTERN.FormatInvariant(segmentKey));
				}
			}
			catch { }
		}

		public virtual string GetLocalizedValue(int languageId, int entityId, string localeKeyGroup, string localeKey)
		{
			if (IsInScope)
			{
				return GetLocalizedValueUncached(languageId, entityId, localeKeyGroup, localeKey);
			}

			if (languageId <= 0)
				return string.Empty;

			var props = GetCacheSegment(localeKeyGroup, localeKey, entityId, languageId);

			if (!props.TryGetValue(entityId, out var val))
			{
				return string.Empty;
			}

			return val;
		}

		protected string GetLocalizedValueUncached(int languageId, int entityId, string localeKeyGroup, string localeKey)
		{
			if (languageId <= 0)
				return string.Empty;

			if (_prefetchedCollections.TryGetValue(localeKeyGroup, out var collection))
			{
				var cachedItem = collection.Find(languageId, entityId, localeKey);
				if (cachedItem != null)
				{
					return cachedItem.LocaleValue;
				}
			}

			var query = from lp in _localizedPropertyRepository.TableUntracked
						where
							lp.EntityId == entityId &&
							lp.LocaleKey == localeKey &&
							lp.LocaleKeyGroup == localeKeyGroup &&
							lp.LanguageId == languageId
						select lp.LocaleValue;

			return query.FirstOrDefault().EmptyNull();
		}

		public virtual IList<LocalizedProperty> GetLocalizedProperties(int entityId, string localeKeyGroup)
        {
            if (localeKeyGroup.IsEmpty())
                return new List<LocalizedProperty>();

            var query = from x in _localizedPropertyRepository.Table
                        orderby x.Id
                        where x.EntityId == entityId && x.LocaleKeyGroup == localeKeyGroup
                        select x;

            var props = query.ToList();
            return props;
        }

		public virtual void PrefetchLocalizedProperties(string localeKeyGroup, int languageId, int[] entityIds, bool isRange = false, bool isSorted = false)
		{
			if (languageId == 0)
				return;

			var collection = GetLocalizedPropertyCollectionInternal(localeKeyGroup, languageId, entityIds, isRange, isSorted);
			
			if (collection.Count > 0)
			{
				if (_prefetchedCollections.TryGetValue(localeKeyGroup, out var existing))
				{
					collection.MergeWith(existing);
				}
				else
				{
					_prefetchedCollections[localeKeyGroup] = collection;
				}
			}
		}

		public virtual LocalizedPropertyCollection GetLocalizedPropertyCollection(string localeKeyGroup, int[] entityIds, bool isRange = false, bool isSorted = false)
		{
			return GetLocalizedPropertyCollectionInternal(localeKeyGroup, 0, entityIds, isRange, isSorted);
		}

		public virtual LocalizedPropertyCollection GetLocalizedPropertyCollectionInternal(string localeKeyGroup, int languageId, int[] entityIds, bool isRange = false, bool isSorted = false)
		{
			Guard.NotEmpty(localeKeyGroup, nameof(localeKeyGroup));

			using (var scope = new DbContextScope(ctx: _localizedPropertyRepository.Context, proxyCreation: false, lazyLoading: false))
			{
				var query = from x in _localizedPropertyRepository.TableUntracked
							where x.LocaleKeyGroup == localeKeyGroup
							select x;

				if (entityIds != null && entityIds.Length > 0)
				{
					if (isRange)
					{
						var min = isSorted ? entityIds[0] : entityIds.Min();
						var max = isSorted ? entityIds[entityIds.Length - 1] : entityIds.Max();

						query = query.Where(x => x.EntityId >= min && x.EntityId <= max);
					}
					else
					{
						query = query.Where(x => entityIds.Contains(x.EntityId));
					}
				}

				if (languageId > 0)
				{
					query = query.Where(x => x.LanguageId == languageId);
				}

				return new LocalizedPropertyCollection(localeKeyGroup, query.ToList());
			}
		}

		protected virtual LocalizedProperty GetLocalizedProperty(int languageId, int entityId, string localeKeyGroup, string localeKey)
		{
			var query = from lp in _localizedPropertyRepository.Table
						where
							lp.EntityId == entityId &&
							lp.LocaleKey == localeKey &&
							lp.LocaleKeyGroup == localeKeyGroup &&
							lp.LanguageId == languageId
						select lp;

			return query.FirstOrDefault();
		}

		public virtual void InsertLocalizedProperty(LocalizedProperty property)
        {
			Guard.NotNull(property, nameof(property));

			// db
			_localizedPropertyRepository.Insert(property);
			HasChanges = true;

			// cache
			ClearCacheSegment(property.LocaleKeyGroup, property.LocaleKey, property.EntityId, property.LanguageId);
        }

        public virtual void UpdateLocalizedProperty(LocalizedProperty property)
        {
			Guard.NotNull(property, nameof(property));

			// db
			_localizedPropertyRepository.Update(property);
			HasChanges = true;

			// cache
			ClearCacheSegment(property.LocaleKeyGroup, property.LocaleKey, property.EntityId, property.LanguageId);
		}

		public virtual void DeleteLocalizedProperty(LocalizedProperty property)
		{
			Guard.NotNull(property, nameof(property));

			// cache
			ClearCacheSegment(property.LocaleKeyGroup, property.LocaleKey, property.EntityId, property.LanguageId);

			// db
			_localizedPropertyRepository.Delete(property);
			HasChanges = true;
		}

		public virtual LocalizedProperty GetLocalizedPropertyById(int localizedPropertyId)
		{
			if (localizedPropertyId == 0)
				return null;

			var localizedProperty = _localizedPropertyRepository.GetById(localizedPropertyId);
			return localizedProperty;
		}

		public virtual void SaveLocalizedValue<T>(
			T entity,
            Expression<Func<T, string>> keySelector,
            string localeValue,
            int languageId) where T : BaseEntity, ILocalizedEntity
        {
            SaveLocalizedValue<T, string>(entity, keySelector, localeValue, languageId);
        }

        public virtual void SaveLocalizedValue<T, TPropType>(
			T entity,
            Expression<Func<T, TPropType>> keySelector,
            TPropType localeValue,
            int languageId) where T : BaseEntity, ILocalizedEntity
        {
			Guard.NotNull(entity, nameof(entity));
			Guard.NotZero(languageId, nameof(languageId));

            var member = keySelector.Body as MemberExpression;
            if (member == null)
            {
                throw new ArgumentException($"Expression '{keySelector}' refers to a method, not a property.");
            }

            var propInfo = member.Member as PropertyInfo;
            if (propInfo == null)
            {
                throw new ArgumentException($"Expression '{keySelector}' refers to a field, not a property.");
            }

            var keyGroup = typeof(T).Name;
            var key = propInfo.Name;
			var valueStr = localeValue.Convert<string>();
			var prop = GetLocalizedProperty(languageId, entity.Id, keyGroup, key);

            if (prop != null)
            {
                if (valueStr.IsEmpty())
                {
                    // delete
                    DeleteLocalizedProperty(prop);
                }
                else
                {
                    // update
					if (prop.LocaleValue != valueStr)
					{
						prop.LocaleValue = valueStr;
						UpdateLocalizedProperty(prop);
					}
                }
            }
            else
            {
                if (valueStr.HasValue())
                {
                    // insert
                    prop = new LocalizedProperty
                    {
                        EntityId = entity.Id,
                        LanguageId = languageId,
                        LocaleKey = key,
                        LocaleKeyGroup = keyGroup,
                        LocaleValue = valueStr
                    };
                    InsertLocalizedProperty(prop);
                }
            }
        }

		private string BuildCacheSegmentKey(string segment, int languageId)
		{
			return String.Format(LOCALIZEDPROPERTY_SEGMENT_KEY, segment, languageId);
		}

		private string GetSegmentKeyPart(string localeKeyGroup, string localeKey, int entityId)
		{
			return GetSegmentKeyPart(localeKeyGroup, localeKey, entityId, out var minId, out var maxId);
		}

		private string GetSegmentKeyPart(string localeKeyGroup, string localeKey, int entityId, out int minId, out int maxId)
		{
			maxId = entityId.GetRange(_performanceSettings.CacheSegmentSize, out minId);
			return (localeKeyGroup + "." + localeKey + "." + maxId.ToString()).ToLowerInvariant();
		}
	}
}