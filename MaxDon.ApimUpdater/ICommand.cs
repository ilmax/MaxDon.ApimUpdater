namespace MaxDon.ApimUpdater;

public interface ICommand
{
    Task<int> ExecuteAsync();
}