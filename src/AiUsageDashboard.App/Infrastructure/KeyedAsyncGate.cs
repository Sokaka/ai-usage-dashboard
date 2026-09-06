using System.Collections.Concurrent;

namespace AiUsageDashboard.App.Infrastructure;

internal sealed class KeyedAsyncGate<TKey>
	where TKey : notnull
{
	private sealed class Entry
	{
		internal SemaphoreSlim Gate { get; } = new(1, 1);

		internal int ReferenceCount { get; set; }

		internal bool IsEvictionRequested { get; set; }

		internal bool IsRetired { get; set; }

		internal object SyncRoot { get; } = new();
	}

	private sealed class Lease : IDisposable
	{
		private readonly TKey _key;
		private Entry? _entry;
		private KeyedAsyncGate<TKey>? _owner;

		internal Lease(
			KeyedAsyncGate<TKey> owner,
			TKey key,
			Entry entry)
		{
			_owner = owner;
			_key = key;
			_entry = entry;
		}

		public void Dispose()
		{
			KeyedAsyncGate<TKey>? owner = Interlocked.Exchange(ref _owner, null);
			Entry? entry = Interlocked.Exchange(ref _entry, null);

			if ((owner is null) || (entry is null))
			{
				return;
			}

			entry.Gate.Release();
			owner.ReleaseReference(_key, entry);
		}
	}

	private readonly ConcurrentDictionary<TKey, Entry> _entries;

	internal int Count => _entries.Count;

	internal KeyedAsyncGate(IEqualityComparer<TKey>? comparer = null)
	{
		_entries = new ConcurrentDictionary<TKey, Entry>(
			comparer ?? EqualityComparer<TKey>.Default);
	}

	internal bool ContainsKey(TKey key)
	{
		return _entries.ContainsKey(key);
	}

	internal async ValueTask<IDisposable> EnterAsync(
		TKey key,
		CancellationToken cancellationToken,
		bool evictWhenIdle = false)
	{
		Entry entry;

		while (true)
		{
			entry = _entries.GetOrAdd(key, static _ => new Entry());

			lock (entry.SyncRoot)
			{
				if (entry.IsRetired)
				{
					continue;
				}

				entry.ReferenceCount++;
				entry.IsEvictionRequested |= evictWhenIdle;
				break;
			}
		}

		try
		{
			await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			return new Lease(this, key, entry);
		}
		catch
		{
			ReleaseReference(key, entry);
			throw;
		}
	}

	internal void RequestEviction(TKey key)
	{
		if (!_entries.TryGetValue(key, out Entry? entry))
		{
			return;
		}

		lock (entry.SyncRoot)
		{
			entry.IsEvictionRequested = true;
			TryRetireEntry(key, entry);
		}
	}

	private void ReleaseReference(TKey key, Entry entry)
	{
		lock (entry.SyncRoot)
		{
			entry.ReferenceCount--;
			TryRetireEntry(key, entry);
		}
	}

	private void TryRetireEntry(TKey key, Entry entry)
	{
		if (!entry.IsEvictionRequested ||
			(entry.ReferenceCount != 0) ||
			entry.IsRetired)
		{
			return;
		}

		ICollection<KeyValuePair<TKey, Entry>> entries = _entries;

		if (entries.Remove(new KeyValuePair<TKey, Entry>(key, entry)))
		{
			entry.IsRetired = true;
		}
	}
}
