using System;
// Copied from Hangfire.Redis.StackExchange commit da8e39a33df204900afc30aeb65110f76f081c55.
// See ../NOTICE.md and ../License.md for upstream copyright and LGPLv3 terms.
using System.Threading;

namespace Hangfire.Redis.Tests.Utils
{
    public static class StaticFakeJobs
    {
        public static string Work(int identifier, int waitTime)
        {
            Thread.Sleep(waitTime);
            var jobResult = String.Format("{0} - {1} Job done after waiting {2} ms", DateTime.Now.ToString("hh:mm:ss fff"), identifier, waitTime);
            Console.WriteLine(jobResult);
            return jobResult;
        }
    }
}
