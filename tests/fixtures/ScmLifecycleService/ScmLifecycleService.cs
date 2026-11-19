#:property TargetFramework=net10.0-windows
#:property PublishAot=false
#:package System.ServiceProcess.ServiceController@10.0.14

using System.ServiceProcess;

using var service = new LifecycleService(args.Single());
ServiceBase.Run(service);

sealed class LifecycleService : ServiceBase
{
    public LifecycleService(string name)
    {
        ServiceName = name;
        CanStop = true;
        AutoLog = false;
    }
}
