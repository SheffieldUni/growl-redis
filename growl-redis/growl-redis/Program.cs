using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ServiceStack.Redis;
using System.Threading;
using System.Diagnostics;
using Growl.Connector;
using System.Web.Script.Serialization;

//TODO: Refactor to one style of casing 

namespace growl_redis
{
    class Program
    {
        static string redis_server;
        static string redis_port;
        static string script_file;
        static string app_name;
        //static int thread_counter = 0;

        static Growl.Connector.GrowlConnector growl;
        static Growl.Connector.Application app;

        static void Main(string[] args)
        {
            Dictionary<string, Parameter> parameters = new Dictionary<string, Parameter>();
            if (args != null)
            {
                foreach (string arg in args)
                {
                    Parameter p = GetParameterValue(arg);
                    if (p.Argument != null) parameters.Add(p.Argument, p);
                }
            }
            else
            {
                throw new System.ArgumentException("Parameters cannot be null");
            }

            if (parameters.ContainsKey("/server"))
            {
                redis_server = parameters["/server"].Value.ToLower();
            }
            else
            {
                throw new System.ArgumentException("Server name required /server:server");
            }

            if (parameters.ContainsKey("/port"))
            {
                redis_port = parameters["/port"].Value.ToLower();
            }
            else
            {
                redis_port = "6379";
            }

            if (parameters.ContainsKey("/script"))
            {
                script_file = parameters["/script"].Value.ToLower();
            }
            else
            {
                script_file = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + @"\subscriptions.ps1";
            }

            if (parameters.ContainsKey("/appname"))
            {
                app_name = parameters["/appname"].Value.ToLower();
            }
            else
            {
                throw new System.ArgumentException("App name required /appname:appname");
            }

            Console.WriteLine("Using redis server {0}:{1}", redis_server, redis_port);
            Console.WriteLine("Using subscription script at {0}", script_file);

            // Run configuration and determine a list of subscriptions
            Runspace rs = RunspaceFactory.CreateRunspace();
            rs.Open();
            PowerShell ps = PowerShell.Create();
            ps.Runspace = rs;

            //TODO: check for file existence
            string script = System.IO.File.ReadAllText(script_file);
            
            ps.AddScript(script);

            List<string> subscriptions = new List<string>();

            foreach(PSObject result in ps.Invoke())
            {
                subscriptions.Add(result.ToString().ToLower());
            }

            rs.Close();

            // Sort out the Growl application
            growl = new GrowlConnector();
            app = new Growl.Connector.Application(app_name);

            NotificationType[] notification_types = new NotificationType[subscriptions.Count];

            // Check whether Growl is running on the local machine
            if (!growl.IsGrowlRunning())
            {
                // Growl is not running so we should just bomb out
                Environment.Exit(1);
            }
            
            // Connect to redis server
            using (var redisConsumer = new RedisClient(redis_server, Convert.ToInt32(redis_port)))
            {
                int i = 0;

                //Subscribe to channels
                foreach (string channel_name in subscriptions)
                {
                    // Add channel/subscription to the notification types array to register with Growl
                    notification_types[i] = new NotificationType(channel_name, channel_name, null, true);
                    i++;
                }
                    
                // Setup notification thread
                ThreadPool.QueueUserWorkItem(x =>
                {
                    using (var subscription = redisConsumer.CreateSubscription())
                    {
                        // Define callbacks for pubsub events
                        subscription.OnSubscribe = channel =>
                        {
                            Debug.Print("Subscribed to {0}", channel);
                        };

                        subscription.OnUnSubscribe = channel =>
                        {
                            Debug.Print("UnSubscribed to {0}", channel);
                        };

                        // Message callback
                        subscription.OnMessage = (channel, message) =>
                        {
                            Debug.Print("Message recieved on channel {0}, message: {1}", channel, message);

                            bool result = SendGrowl(channel, message);
                            Debug.Print("SendGrowl returned: {0}", result);
                        };

                        // Register all of the channels as growl notification types
                        growl.Register(app, notification_types);

                        // Grab any bulletins for the things we're subscribed to and growl them
                        foreach (string channel_name in subscriptions)
                        {
                            string bulletin = redisConsumer.Get<String>(channel_name);
                            if (bulletin != null)
                            {
                                bool result = SendGrowl(channel_name, bulletin);
                                Debug.Print("SendGrowl returned: {0}", result);
                            }
                        }

                        // Subscribe to all the channels in redis
                        // Sits in a loop in case redis goes away
                        while (true)
                        {
                            // Have a sleep 
                            Thread.Sleep(500);

                            try
                            {
                                subscription.SubscribeToChannels(subscriptions.ToArray<String>());
                            } 
                            catch (RedisException) 
                            {
                                // Do nothing
                            }
                        }
                    }
                });           
                
            }

            // Just keep us alive while the notification thread runs
            while (true)
            {
                Thread.Sleep(5000);
            }
        }

        // Parse the parameter into a struct should be /name:val
        private static Parameter GetParameterValue(string argument)
        {
            if (argument.StartsWith("/"))
            {
                string val = "";
                string[] parts = argument.Split(new char[] { ':' }, 2);
                if (parts.Length == 2)
                {
                    val = parts[1];
                    if (val.StartsWith("\"") && val.EndsWith("\""))
                    {
                        val = val.Substring(1, val.Length - 2);
                    }
                }
                return new Parameter(parts[0], val);
            }
            return Parameter.Empty;
        }

        // Describes a parameter
        private struct Parameter
        {
            public static Parameter Empty = new Parameter(null, null);

            public Parameter(string arg, string val)
            {
                this.Argument = arg;
                this.Value = val;
            }

            public string Argument;
            public string Value;
        }

        // Parse a message and notify
        private static Boolean SendGrowl(string channel, string message)
        {
            // Turn the JSON return into a Hashtable
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Hashtable message_hash = new Hashtable();
            try
            {
                message_hash = serializer.Deserialize<Hashtable>(message);
            }
            catch
            {
                // Just don't growl this message
                return false;
            }
            
            if (!message_hash.ContainsKey("Caption") ||!message_hash.ContainsKey("Message"))
            {
                return false;
            }

            // Build the notification
            Notification notification = new Notification(app.Name, channel, DateTime.Now.Ticks.ToString(), message_hash["Caption"].ToString(), message_hash["Message"].ToString());

            if (!message_hash.ContainsKey("Priority"))
            {
                message_hash.Add("Priority", "Normal");
            }

            notification.Priority = (Priority)Enum.Parse(typeof(Priority), message_hash["Priority"].ToString());

            if (message_hash.ContainsKey("Sticky"))
            {
                notification.Sticky = true;
            }

            if (message_hash.ContainsKey("Icon"))
            {
                Debug.Print("Icon: {0} ", message_hash["Icon"].ToString());
                notification.Icon = message_hash["Icon"].ToString();
            }

            // Tell Growl to notify
            growl.Notify(notification);
            return true;
        }
    }
}
