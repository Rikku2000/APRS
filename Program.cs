using System;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.Threading;

#if APRSGATEWAY
#if GUI
using System.Windows.Forms;

namespace APRSForwarder
{
    class Program
    {
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new APRSGateWayGUI());
        }
    }
}
#else
namespace APRSForwarder
{
    class Program
    {        
        static void Main(string[] args)
        {
			APRSGateWay gateway = new APRSGateWay();
            gateway.Start();
            // gateway.Stop();
        }
    }
}
#endif
#else
namespace APRSWebServer
{
    class Program
    {        
        static void Main(string[] args)
        {
			APRSServer server = new APRSServer();
            server.Start();
            Console.WriteLine("Type exit to Exit:");
            while (true) if(Console.ReadLine() == "exit") break;
            Console.WriteLine("exiting...");
            server.Stop();
        }
    }
}
#endif
