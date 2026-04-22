using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Seeing.Session.Core
{
    // Lightweight concrete implementation of ISession used by the factory.
    internal class Session : ISession
    {
        private SessionData _data;
        private readonly Seeing.Session.Storage.ISessionStore? _store;

        public Session(SessionData data, Storage.ISessionStore? store)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _store = store;
        }

        public string Id => _data.Id;

        public string Title
        {
            get => _data.Title;
            set
            {
                _data.Title = value;
                _data.UpdatedAt = DateTime.Now;
            }
        }

        public DateTime CreatedAt => _data.CreatedAt;
        public DateTime UpdatedAt => _data.UpdatedAt;

        public string PartitionId
        {
            get => _data.PartitionId;
            set
            {
                _data.PartitionId = value;
                _data.UpdatedAt = DateTime.Now;
            }
        }

        public Task SaveAsync()
        {
            _data.UpdatedAt = DateTime.Now;
            if (_store != null)
            {
                return _store.SaveAsync(_data);
            }
            return Task.CompletedTask;
        }

        public Task LoadAsync()
        {
            // For this in-memory/simple implementation, Load is a no-op
            // as data is already present on construction.
            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            // For the simple session, resume is a no-op unless we have a store that
            // can refresh data. If a store exists, reload the latest data.
            if (_store != null)
            {
                return _store.LoadAsync(_data.Id).ContinueWith(t =>
                {
                    var loaded = t.Result;
                    if (loaded != null)
                    {
                        _data = loaded;
                    }
                });
            }
            return Task.CompletedTask;
        }

        public Task DestroyAsync()
        {
            if (_store != null)
            {
                return _store.DeleteAsync(_data.Id);
            }
            return Task.CompletedTask;
        }

        public TValue GetState<TValue>(string key)
        {
            if (_data.State != null && _data.State.TryGetValue(key, out var v))
            {
                try
                {
                    // Try direct cast for common types
                    return (TValue)Convert.ChangeType(v, typeof(TValue));
                }
                catch
                {
                    // Fallback: attempt JSON deserialization for complex types
                    try
                    {
                        return JsonSerializer.Deserialize<TValue>(v)!;
                    }
                    catch
                    {
                        return default!;
                    }
                }
            }
            return default!;
        }

        public void SetState<TValue>(string key, TValue value)
        {
            if (_data.State == null)
            {
                _data.State = new Dictionary<string, string>();
            }
            // Store as string representation to keep the dictionary simple
            _data.State[key] = value switch
            {
                string s => s,
                null => null!,
                _ => JsonSerializer.Serialize(value)
            };
            _data.UpdatedAt = DateTime.Now;
        }

        public string Serialize()
        {
            return JsonSerializer.Serialize(_data);
        }

        public Task DeserializeAsync(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return Task.CompletedTask;
            var obj = JsonSerializer.Deserialize<SessionData>(payload);
            if (obj != null)
            {
                _data = obj;
            }
            return Task.CompletedTask;
        }
    }
}
