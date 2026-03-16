using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    public static class SystemLog
    {
        private static ISystemLogProvider _systemLogProvider => throw new NotImplementedException();// App.Services.GetService<ISystemLogProvider>();

        public static void Write(string entry) =>
            _systemLogProvider?.Write(entry);
        public static void Write(string entry, ICoreObject linkedObject) =>
            _systemLogProvider?.Write(entry, linkedObject);
        public static void Write(string entry, SystemLogType logType) =>
            _systemLogProvider?.Write(entry, logType);
        public static void Write(string entry, SystemLogType logType, ICoreObject linkedObject) =>
            _systemLogProvider?.Write(entry, logType, linkedObject);
    }
    public static class SecurityLog
    {
        private static ISecurityLogProvider _securityLogProvider => throw new NotImplementedException();// App.Services.GetService<ISecurityLogProvider>();

        public static void Write(string entry) =>
            _securityLogProvider?.Write(entry);
        public static void Write(string entry, IUser user) =>
            _securityLogProvider?.Write(entry, user);
        public static void Write(string entry, SecurityLogType logType) =>
            _securityLogProvider?.Write(entry, logType);
        public static void Write(string entry, SecurityLogType logType, IUser user) =>
            _securityLogProvider?.Write(entry, logType, user);
    }


    public interface ISystemLogProvider
    {
        void Write(string entry);
        void Write(string entry, ICoreObject linkedObject);
        void Write(string entry, SystemLogType logType);
        void Write(string entry, SystemLogType logType, ICoreObject linkedObject);

    }
    public interface ISecurityLogProvider
    {
        void Write(string entry);
        void Write(string entry, IUser user);
        void Write(string entry, SecurityLogType logType);
        void Write(string entry, SecurityLogType logType, IUser user);
    }

    public interface ISystemLog : ICoreObject
    {
        string Entry { get; }
        SystemLogType Type { get; }
        IUser User { get; }
        string Source { get; }
        ICoreObject[] LinkedObjects { get; }
    }
    public interface ISecurityLog : ICoreObject
    {
        string Entry { get; }
        SecurityLogType Type { get; }
        IUser User { get; }
        string Source { get; }
    }

    //public enum LogType
    //{
    //    Informational = 0,
    //    Security = 1,
    //    Error = 2,
    //    CreateOrUpdate = 3,
    //}
    public enum SecurityLogType
    {
        Informational = 0,
        Logon = 1,
        Logoff = 2,
        AuthenticationFailure = 3,
        ReadAccessDenied = 4,
        WriteAccessDenied = 5,
        AccessDenied = 6,
        AccountLocked = 7,
        Exception = 99,
    }
    public enum SystemLogType
    {
        Informational = 0,
        Security = 1,
        Error = 2,
        CreateOrUpdate = 3,
        Create = 4,
        Update = 5,
    }
}
