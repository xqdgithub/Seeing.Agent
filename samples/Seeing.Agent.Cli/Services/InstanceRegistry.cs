using System.Text.Json;

namespace Seeing.Agent.Cli.Services;

public sealed class InstanceRegistry
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public InstanceRegistry(string? directory = null)
    {
        var dir = directory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "instances.json");
    }

    public string FilePath => _filePath;

    public List<InstanceRecord> Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return new List<InstanceRecord>();
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<RegistryDocument>(json)?.Instances
                    ?? new List<InstanceRecord>();
            }
            catch
            {
                return new List<InstanceRecord>();
            }
        }
    }

    public void Add(InstanceRecord record)
    {
        lock (_lock)
        {
            var list = Load();
            list.Add(record);
            SaveCore(list);
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(i => i.Id == id);
            SaveCore(list);
        }
    }

    public List<InstanceRecord> PruneDead()
    {
        lock (_lock)
        {
            var list = Load();
            var alive = list.Where(i => IsProcessAlive(i.Pid)).ToList();
            if (alive.Count != list.Count) SaveCore(alive);
            return alive;
        }
    }

    private void SaveCore(List<InstanceRecord> list)
    {
        File.WriteAllText(
            _filePath,
            JsonSerializer.Serialize(
                new RegistryDocument { Instances = list },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private sealed class RegistryDocument
    {
        public List<InstanceRecord> Instances { get; set; } = new();
    }
}