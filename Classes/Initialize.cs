using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    //public class Initializer(IOptionsMonitor<NucleusOptions> options)
    //{
    //    private readonly NucleusOptions _options = options.CurrentValue;
    //    public Initializer()
    //    {
    //        // Default constructor for cases where IOptionsMonitor is not available
    //    }
    //    public void Initialize()
    //    {
    //        // Initialization logic can be added here if needed
    //    }

    //    private static string? _Source = null;
    //    public static string GetSource()
    //    {
    //        if (_Source == null)
    //        {
    //            _Source = Process.GetCurrentProcess().MainModule?.ModuleName?.Replace(".exe", "") ?? "Nucleus";
    //        }
    //        return _Source;
    //    }
    //}

    //public abstract class Initializer()
    //{
    //    private IObjectDataManager? _dataManager = null;
    //    private List<Type> _mandatoryTypes = [typeof(SystemLog), typeof(SecurityLog)];




    //    public static void InitializeDataProvider(IObjectDataManager dataManager, Type[]? objectTypes = null)
    //    {
    //        // Initialize data provider
    //        var dataProvider = dataManager.DataProvider;
    //        dataProvider.Initialize();
    //    }

    //private static string GetSource()
    //{
    //    return "Nucleus";
    //if(Environment.UserInteractive)
    //if(RuntimeInformation.FrameworkDescription == 

    //if (App.IsHosted)
    //{
    //    string s = "";
    //    try
    //    {
    //        if (HttpContext.Current != null)
    //        {
    //            HttpRequest R = HttpContext.Current.Request;
    //            s += R.Host;
    //            App.AppName = s;
    //            return s;
    //        }
    //        else
    //        {
    //            return App.AppName;
    //        }
    //    }
    //    catch (Exception)
    //    {
    //        return s;
    //    }
    //}
    //else
    //{
    //    if (_Source == null)
    //        _Source = Process.GetCurrentProcess().MainModule.ModuleName.Replace(".exe", "");
    //    return _Source;
    //}
    //}
    //}
}
