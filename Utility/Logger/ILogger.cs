using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Utility.Logger
{
    public interface ILogger
    {
        void LogInfo(string info);
        void LogWarning(string warning);
        void LogError(string error);
    }
}
