using Quartz;

public class Test
{
    public void TestSQLite()
    {
        var builder = SchedulerBuilder.Create();
        builder.UsePersistentStore(store =>
        {
            // Test if UseSQLite exists
            store.UseSQLite("test.db");
        });
    }
}
