﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using PruneLibrary;
using System.Timers;
using System.IO;
using Newtonsoft.Json;

namespace PruneService
{
    public partial class PruneService : ServiceBase
    {
        //Configuration defaults, with times in seconds
        private const uint LogIntervalDefault = 86400;
        private const uint CacheIntervalDefault = 3600;
        private const uint MonitorIntervalDefault = 1;
        private const uint WhitelistIntervalDefault = 60;
        private const uint ConfigIntervalDefault = 60;
        private const string EventSourceName = "Prune";
        private const string EventLogName = "Prune";

        //File and directory paths
        private const string DirectoryPath = @"C:\ProgramData\Prune";
        private const string ConfigPath = DirectoryPath + @"\config.json";
        private const string WhitelistPath = DirectoryPath + @"\whitelist.txt";

        //Times in seconds
        private uint _logInterval = LogIntervalDefault;
        private uint _writeCacheInterval = CacheIntervalDefault;
        private uint _monitorInterval = MonitorIntervalDefault;
        private uint _whitelistCheckInterval = WhitelistIntervalDefault;
        private uint _configCheckInterval = ConfigIntervalDefault;

        //Timers
        private readonly Timer _monitorTimer = new Timer();
        private readonly Timer _whitelistTimer = new Timer();
        private readonly Timer _configTimer = new Timer();

        //List of Prune instance objects, one for each process being monitored
        private readonly Dictionary<int, PruneProcessInstance> _PruneInstances = new Dictionary<int, PruneProcessInstance>();
        private readonly Dictionary<int, string> _processIdToWhitelistEntry = new Dictionary<int, string>();
        private readonly List<int> _finishedInstances = new List<int>();

        public PruneService()
        {
            InitializeComponent();

            eventLog1 = new EventLog();
            if (!EventLog.SourceExists(EventSourceName))
            {
                EventLog.CreateEventSource(EventSourceName, EventLogName);
            }
            eventLog1.Source = EventSourceName;
            eventLog1.Log = EventLogName;
        }

        static void CurrentDomian_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Prune.EventLog.WriteEntry("Unhandled Exception:\n" + (e.ExceptionObject as Exception).Message + "\n" + (e.ExceptionObject as Exception).StackTrace, EventLogEntryType.Error, 5000);
        }

        protected override void OnStart(string[] args)
        {//Set the event log in the static class that the Library can use
            Prune.SetEventLog(eventLog1);

            AppDomain.CurrentDomain.FirstChanceException += (IChannelSender, eventArgs) =>
            {
                Prune.EventLog.WriteEntry(eventArgs.Exception.ToString() + "\n" + eventArgs.Exception.StackTrace, EventLogEntryType.Error, 4000);
            };

            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomian_UnhandledException);

            eventLog1.WriteEntry("Prune service starting", EventLogEntryType.Information, 1);

            //create the ProgramData directory if it does not already exist
            Directory.CreateDirectory(DirectoryPath);

            //Read the config file and get it's values
            ReadConfigFile();

            lock (_PruneInstances)
            {
                //Parse the whitelist and add all of the Prune process instances to the master list, and to the ETW list
                Dictionary<int, PruneProcessInstance> list = ParseWhitelist();

                foreach (var instEntry in list)
                {
                    _PruneInstances.Add(instEntry.Key, instEntry.Value);
                    Prune.AddEtwCounter(instEntry.Key);
                }
            }

            //initialize each of the Prune instances
            try
            {
                lock (_PruneInstances)
                {
                    foreach (PruneProcessInstance inst in _PruneInstances.Values)
                    {
                        inst.InitializeInstance();
                    }
                }
            }
            catch (Exception e)
            {
                eventLog1.WriteEntry("Error initializing Prune instances\n" + e.Message + "\n" + e.Source,
                    EventLogEntryType.Error, 201);
            }

            try
            {
                //Check for old, unlogged files as the service starts
                ReadOldFiles();
            }
            catch (Exception e)
            {
                eventLog1.WriteEntry("Error reading old files\n" + e.Message, EventLogEntryType.Error, 201);
            }

            //Start the etw sesstion
            Prune.StartEtwSession(true);

            //create the timer for gathering data from the performance counters
            try
            {
                _monitorTimer.Elapsed += (sender, e) => { OnTimerMonitor(); };
                _monitorTimer.Start();
            }
            catch (Exception e)
            {
                eventLog1.WriteEntry("Error starting the process monitoring timer\n" + e.Message, EventLogEntryType.Error, 201);
                return;
            }

            //Create and start the timer to monitor the whitelist file
            try
            {
                _whitelistTimer.Elapsed += (sender, e) => { OnTimerWhitelist(); };
                _whitelistTimer.Start();
            }
            catch (Exception e)
            {
                eventLog1.WriteEntry("Error starting the whitelist check timer\n" + e.Message, EventLogEntryType.Error, 201);
                //cleanup already running timers before exiting
                _monitorTimer.Stop();
                return;
            }

            //Create and start the timer to monitor the config file
            try
            {
                _configTimer.Elapsed += (sender, e) => { ReadConfigFile(); };
                _configTimer.Start();
            }
            catch (Exception e)
            {
                eventLog1.WriteEntry("Error starting the configuration file check timer\n" + e.Message, EventLogEntryType.Error, 201);
                //cleanup already running timers before exiting
                _monitorTimer.Stop();
                _whitelistTimer.Stop();
            }
        }

        //Read the config file and parse the values it contains
        private void ReadConfigFile()
        {
            //check if the config file exists
            if (!File.Exists(ConfigPath))
            {
                try
                {
                    //the configuration file does not exist, so we need to create it with default values
                    //Create the configuration object
                    ServiceConfiguration config = new ServiceConfiguration(LogIntervalDefault, CacheIntervalDefault, MonitorIntervalDefault, WhitelistIntervalDefault, ConfigIntervalDefault);

                    //Create the json string for the configuration object
                    string configString = JsonConvert.SerializeObject(config, Formatting.Indented);

                    //write the config json string to the config file
                    using (StreamWriter sw = new StreamWriter(ConfigPath, false))
                    {
                        sw.Write(configString);
                        sw.Flush();
                    }
                }
                catch (Exception e)
                {
                    eventLog1.WriteEntry("Error while creating config file and writing default setting\n" + e.Message, EventLogEntryType.Error, 201);
                }
            }
            else
            {
                try
                {
                    //The config file exists, so we need to read it

                    //Get the text and parse the json into a ServiceConfiguration object
                    string configFileText = File.ReadAllText(ConfigPath);
                    ServiceConfiguration config = JsonConvert.DeserializeObject<ServiceConfiguration>(configFileText);

                    _logInterval = config.CalculateStatisticsInterval;
                    _writeCacheInterval = config.WriteCacheToFileInterval;
                    _monitorInterval = config.DataRecordingInterval;
                    _whitelistCheckInterval = config.WhitelistCheckInterval;
                    _configCheckInterval = config.ConfigCheckInterval;

                    try
                    {
                        _monitorTimer.Interval = _monitorInterval * 1000;
                        _whitelistTimer.Interval = _whitelistCheckInterval * 1000;
                        _configTimer.Interval = _configCheckInterval * 1000;
                    }
                    catch (Exception e)
                    {
                        eventLog1.WriteEntry("Error setting timer interval times from the configuration file input\n" + e.Message, EventLogEntryType.Error, 201);
                    }
                }
                catch (Exception e)
                {
                    eventLog1.WriteEntry("Error reading configuration file\n" + e.Message, EventLogEntryType.Error, 201);
                }
            }
        }

        //Processes cache files on service start-up, logging statistics on those that have missed their logging window
        //and adding those that are still inside of the logging window to the unlogged list
        private void ReadOldFiles()
        {

            DateTime earliestStartTime = DateTime.MaxValue;
            DateTime latestFinishTime = DateTime.MinValue;

            //Get all of the process directories
            string[] directories;

            try
            {
                directories = Directory.GetDirectories(DirectoryPath);
            }
            catch (Exception e)
            {
                eventLog1.WriteEntry("Error fetching process specific directories for processing of pre-existing files\n" + e.Message, EventLogEntryType.Error, 201);
                return;
            }

            //Look through each of the process directories
            foreach (string directory in directories)
            {

                //Get the process name, which is the top level directory
                string[] directorySplit = directory.Split('\\');
                string processName = directorySplit[directorySplit.Length - 1];

                //Get all json files in the process directory
                string[] files = Directory.GetFiles(directory, "*.json");

                //Go to the next directory if there are no files
                if (files.Length == 0)
                {
                    continue;
                }

                try
                {
                    //For each file in the process directory
                    foreach (string file in files)
                    {

                        //get the start and end times of the file
                        string[] split = file.Split('-');
                        DateTime tempStart = DateTime.ParseExact(split[1], "yyyyMMdd_HHmmss", null);
                        DateTime tempEnd = DateTime.ParseExact(split[2].Split('.')[0], "yyyyMMdd_HHmmss", null);

                        //Store the earliest start time and the latest finish time of all json files in this directory
                        if (DateTime.Compare(tempStart, earliestStartTime) < 0)
                        {
                            earliestStartTime = tempStart;
                        }
                        if (DateTime.Compare(tempEnd, latestFinishTime) > 0)
                        {
                            latestFinishTime = tempEnd;
                        }
                    }
                }
                catch (Exception e)
                {
                    eventLog1.WriteEntry("Error finding the earliest start and latest finished times of pre-existing files\n" + e.Message, EventLogEntryType.Error, 201);
                }

                //Calculate the amount of time between earliest start and latest finish times
                TimeSpan difference = latestFinishTime.Subtract(earliestStartTime);
                double differenceSeconds = difference.TotalSeconds;

                //calculate the number of log intervals that fall within that time span
                int numberOfIntervals = (int)Math.Floor(differenceSeconds / _logInterval);

                //If there is a partial interval, add one to include it as the above math will round down to the nearest whole interval
                if (((long)differenceSeconds % _logInterval) != 0)
                {
                    numberOfIntervals++;
                }

                //For the following arrays, index i represents the i-th log period 
                //An array of lists that store file names that need to be logged
                Dictionary<string, int>[] fileLists = new Dictionary<string, int>[numberOfIntervals];
                for (int i = 0; i < fileLists.Length; i++)
                {
                    fileLists[i] = new Dictionary<string, int>();
                }

                //Arrays of date times that store start and end times for different intervals
                DateTime[] logIntervalEndTimes = new DateTime[numberOfIntervals];
                DateTime[] logIntervalStartTimes = new DateTime[numberOfIntervals];
                bool cacheLastInterval = false;

                //for each log period
                for (int i = 0; i < numberOfIntervals; i++)
                {
                    //For the first one, the start is the earliest start and the end time is the interval finish time
                    if (i == 0)
                    {
                        logIntervalEndTimes[0] = Prune.CalculateIntervalFinishTime(earliestStartTime, _logInterval);
                        logIntervalStartTimes[0] = earliestStartTime;
                    }
                    else //Otherwise it starts at the finish time of the previous interval
                    {
                        logIntervalEndTimes[i] = Prune.CalculateIntervalFinishTime(logIntervalEndTimes[i - 1], _logInterval);
                        logIntervalStartTimes[i] = logIntervalEndTimes[i - 1];
                    }

                    if (i == numberOfIntervals - 1)
                    {
                        if (DateTime.Compare(logIntervalEndTimes[i], DateTime.Now) > 0)
                        {
                            cacheLastInterval = true;
                        }
                    }
                }

                try
                {
                    //For each json file
                    foreach (string fileName in files)
                    {
                        string[] fileNameSplit = fileName.Split('-');

                        //Get the end time of the file
                        string endtimeString = fileNameSplit[2].Split('.')[0].Trim();
                        DateTime endTime = DateTime.ParseExact(endtimeString, "yyyyMMdd_HHmmss", null);

                        //Get the PID
                        int procId = int.Parse(fileNameSplit[0].Split('_')[1]);

                        //If the end time of the file is less than the log time of the interval, add it to that intervals log list
                        for (int i = 0; i < logIntervalEndTimes.Length; i++)
                        {
                            if (DateTime.Compare(endTime, logIntervalEndTimes[i]) <= 0)
                            {
                                fileLists[i].Add(fileName, procId);
                                break;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    eventLog1.WriteEntry("Error parsing the pre-existing files for process " + processName + "\n" + e.Message, EventLogEntryType.Error, 201);
                }

                //This runs if the most recent old files are in the current interval and need to be cached rather than logged
                if (cacheLastInterval)
                {

                    //Loop through each of the PIDs
                    foreach (int pid in fileLists.Last().Values.Distinct())
                    {
                        List<string> processFiles = new List<string>();

                        //Get all files and add the ones with matching PIDS to the processFiles list
                        foreach (KeyValuePair<string, int> entry in fileLists.Last())
                        {
                            if (entry.Value == pid)
                            {
                                processFiles.Add(entry.Key);
                            }
                        }

                        //Add all files in processFiles to the process's cache, creating a new Prune instance if needed
                        lock (_PruneInstances)
                        {
                            if (_PruneInstances.ContainsKey(pid))
                            {
                                _PruneInstances[pid].AddUnloggedCacheFiles(processFiles);
                            }
                            else
                            {
                                string whitelistEntry = processFiles[0].Split('-')[0].Split('_')[0];
                                PruneProcessInstance rr = new PruneProcessInstance(false, pid, whitelistEntry,
                                    _writeCacheInterval, _logInterval, DirectoryPath);
                                rr.LogDataFromFiles(processFiles, true);
                            }
                        }
                    }

                    //The last file was just cached, so remove it
                    fileLists[fileLists.Length - 1] = null;
                }

                //For each list, log the files in that list
                foreach (Dictionary<string, int> dict in fileLists)
                {
                    if (dict != null)
                    {
                        foreach (int pid in dict.Values.Distinct())
                        {
                            List<string> filesList = new List<string>();

                            foreach (KeyValuePair<string, int> entry in dict)
                            {
                                if (entry.Value == pid)
                                {
                                    filesList.Add(entry.Key);
                                }
                            }

                            lock (_PruneInstances)
                            {
                                if (_PruneInstances.ContainsKey(pid))
                                {
                                    _PruneInstances[pid].LogDataFromFiles(filesList, true);
                                }
                                else
                                {
                                    string whitelistEntry = filesList[0].Split('-')[0].Split('_')[0];
                                    PruneProcessInstance rr = new PruneProcessInstance(false, pid, whitelistEntry,
                                        _writeCacheInterval, _logInterval, DirectoryPath);
                                    rr.LogDataFromFiles(filesList, true);
                                }
                            }
                        }
                    }
                }
            }
        }

        //Runs when the service shuts down
        protected override void OnStop()
        {
            lock (_PruneInstances)
            {
                //Write whatever is currently in the cache to a file for all processes being monitored
                foreach (PruneProcessInstance inst in _PruneInstances.Values)
                {
                    inst.DumpCache();
                }
            }

            Prune.DisposeTraceSession();

            eventLog1.WriteEntry("Service is stopping", EventLogEntryType.Information, 1);
        }

        //Timer function used to get data from Performance Counters
        private void OnTimerMonitor()
        {

            //empty the list to prevent duplicate, and thus failed deletions
            _finishedInstances.Clear();

            lock (_PruneInstances)
            {
                foreach (KeyValuePair<int, PruneProcessInstance> entry in _PruneInstances)
                {
                    //If the instance has been flagged as finished, add it to a list
                    if (entry.Value.ProcessFinished)
                    {
                        _finishedInstances.Add(entry.Key);
                    }
                    else //Otherwise, we can call the get data method
                    {
                        entry.Value.GetData();
                    }
                }

                //Loop through the finished instances and remove them from the active instance list
                foreach (int i in _finishedInstances)
                {
                    _PruneInstances.Remove(i);
                    _processIdToWhitelistEntry.Remove(i);
                    Prune.RemoveEtwCounter(i);
                }
            }
        }

        private void OnTimerWhitelist()
        {
            //Parse the whitelist and return map containing all new process instances. 
            foreach (var instEntry in ParseWhitelist())
            {
                //Add the new entry to the master list and then initialize it
                lock (_PruneInstances)
                {
                    _PruneInstances.Add(instEntry.Key, instEntry.Value);
                }

                Prune.AddEtwCounter(instEntry.Key);
                instEntry.Value.InitializeInstance();
            }
        }

        private Dictionary<int, PruneProcessInstance> ParseWhitelist()
        {
            Dictionary<int, PruneProcessInstance> newInstances = new Dictionary<int, PruneProcessInstance>();

            if (File.Exists(WhitelistPath))
            {
                try
                {
                    //if it does, read in all of it's lines
                    string[] lines = File.ReadAllLines(WhitelistPath);

                    //A dictionary so we know which of the currently monitored processes are in the whitelist
                    Dictionary<string, bool> programFoundInWhitelist = new Dictionary<string, bool>();

                    //initialize all members of the list to false
                    foreach (string value in _processIdToWhitelistEntry.Values)
                    {
                        if (!programFoundInWhitelist.ContainsKey(value))
                        {
                            programFoundInWhitelist.Add(value, false);
                        }
                    }

                    Process[] runningProcesses = Process.GetProcesses(".");

                    //look at each line of the whitelist
                    foreach (string line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            //remove starting and ending whitespace
                            string processName = line.Trim();

                            //the idle and system "processes" should not be monitored, so if they are included they get skipped
                            if (processName.Equals("0") || processName.Equals("4") ||
                                processName.Equals("idle", StringComparison.OrdinalIgnoreCase) ||
                                processName.Equals("system", StringComparison.OrdinalIgnoreCase))
                            {
                                eventLog1.WriteEntry("The System (PID 0) and Idle (PID 4) processes cannot be monitored.", EventLogEntryType.Warning, 9);
                                continue;
                            }

                            //if the line isn't a comment
                            if (processName.ToCharArray()[0] != '#')
                            {

                                if (processName.Contains("module=") || processName.Contains("Module="))
                                {
                                    //Split 'module:' off, then trim white space 
                                    string moduleName = processName.Split('.')[0].Trim();
                                    string moduleFile = processName.Split('=')[1].Trim();

                                    if (_processIdToWhitelistEntry.ContainsValue(moduleName))
                                    {
                                        programFoundInWhitelist[moduleName] = true;
                                    }

                                    foreach (Process proc in runningProcesses)
                                    {
                                        //ensure we don't already monitor this process and that this process is not the system or idle process
                                        if (proc.Id != 4 && proc.Id != 0 && !_processIdToWhitelistEntry.ContainsKey(proc.Id))
                                        {
                                            try
                                            {
                                                //collect the modules from for the process
                                                List<Prune.Module> moduleList =
                                                    Prune.NativeMethods.CollectModules(proc);

                                                foreach (Prune.Module module in moduleList)
                                                {
                                                    //Check if this module is the one we are looking for
                                                    if (module.ModuleName.Equals(moduleFile,
                                                        StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        //It is, so add a new instances and log that it was created
                                                        newInstances.Add(proc.Id, new PruneProcessInstance(true,
                                                            proc.Id,
                                                            moduleName, _writeCacheInterval, _logInterval, DirectoryPath));
                                                        _processIdToWhitelistEntry.Add(proc.Id, moduleName);
                                                        eventLog1.WriteEntry("Creating Prune instance for " +
                                                                             moduleName + "_" + proc.Id, EventLogEntryType.Information, 2);

                                                        //We don't need to look at the rest of the modules
                                                        break;
                                                    }
                                                }
                                            }
                                            catch
                                            {
                                                //If we fail to get any modules for some reason, we need to keep going
                                                //We also don't need to report the error, because this will happen any time we try for a protected processes,
                                                //  which may be often
                                                continue;
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    if (_processIdToWhitelistEntry.ContainsValue(processName))
                                    {
                                        programFoundInWhitelist[processName] = true;
                                    }

                                    string tempProcName;
                                    bool nameIsId = false;

                                    //If the name is all digits, it is treated as an ID
                                    if (processName.All(char.IsDigit))
                                    {
                                        nameIsId = true;

                                        //Get the process name from the ID to ensure there is a process tied to this ID currently active
                                        tempProcName = Prune.GetProcessNameFromProcessId(int.Parse(processName));

                                        if (tempProcName == null)
                                        {
                                            //Could not find a name for the given process ID.
                                            //  Assume the process is not active and skip it.
                                            continue;
                                        }
                                    }
                                    else //Otherwise it is treated as a process name
                                    {
                                        tempProcName = processName;
                                    }

                                    //Get all system process objects that have the specified name
                                    Process[] processes = Prune.GetProcessesFromProcessName(tempProcName);

                                    if (processes == null || processes.Length == 0)
                                    {
                                        //Could not find any processes that have the provided name.
                                        //  Assume the process is not started and skip it.
                                        continue;
                                    }

                                    if (nameIsId)
                                    {
                                        int procId = int.Parse(processName);
                                        processName = Prune.GetProcessNameFromProcessId(procId);

                                        if (!_processIdToWhitelistEntry.ContainsKey(procId))
                                        {
                                            //Because an ID was provided, use the ID for the new instance
                                            newInstances.Add(procId,
                                                new PruneProcessInstance(true, procId, processName,
                                                    _writeCacheInterval, _logInterval, DirectoryPath));
                                            _processIdToWhitelistEntry.Add(procId, processName);
                                            eventLog1.WriteEntry("Creating Prune instance for " + processName +
                                                                 "_" + procId, EventLogEntryType.Information, 2);
                                        }
                                    }
                                    else
                                    {
                                        //We were given a process name, so we create a Prune instance for each process instances
                                        foreach (Process proc in processes)
                                        {
                                            int procId = proc.Id;

                                            if (!_processIdToWhitelistEntry.ContainsKey(procId))
                                            {
                                                newInstances.Add(procId,
                                                    new PruneProcessInstance(true, procId, processName,
                                                        _writeCacheInterval, _logInterval, DirectoryPath));
                                                _processIdToWhitelistEntry.Add(procId, processName);
                                                eventLog1.WriteEntry(
                                                    "Creating Prune instance for " + processName + "_" +
                                                    procId, EventLogEntryType.Information, 2);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    //Loop through to see which Prune instances are tied to processes no longer in the whitelist
                    foreach (string key in programFoundInWhitelist.Keys)
                    {
                        if (!programFoundInWhitelist[key])
                        {
                            //Make a copy of the list to iterate over so that we can remove elements from the original list
                            Dictionary<int, string> tempList = new Dictionary<int, string>(_processIdToWhitelistEntry);

                            lock (_PruneInstances)
                            {
                                //loop through the copied list of all elements that we have Prune instances for that are no longer in the whitelist
                                foreach (KeyValuePair<int, string> entry in tempList)
                                {
                                    if (entry.Value == key)
                                    {
                                        //call the finish monitoring method to wrap everything up
                                        _PruneInstances[entry.Key].FinishMonitoring();

                                        //Remove the process from the list of processes monitored by ETW
                                        Prune.RemoveEtwCounter(entry.Key);

                                        //remove it form the active Prune instances
                                        _PruneInstances.Remove(entry.Key);

                                        //remove it from the active whitelist entries
                                        _processIdToWhitelistEntry.Remove(entry.Key);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    eventLog1.WriteEntry("Error while reading and parsing the whitelist file\n" + e.Message, EventLogEntryType.Error, 201);
                }
            }
            else
            {
                try
                {
                    //If the whitelist file does not exist, create a blank text file for future use
                    File.CreateText(WhitelistPath);
                    eventLog1.WriteEntry("No whitelist file existed at 'C:\\ProgramData\\Prune\\whitelist.txt', so a new empty file was created", EventLogEntryType.Information, 8);
                }
                catch (Exception e)
                {
                    eventLog1.WriteEntry("Error creating whitelist file\n" + e.Message, EventLogEntryType.Error, 201);
                }
            }

            return newInstances;
        }

        //Data holding class used for configurating the service
        //It is serialized to create the config file and deserialized to read the config file
        //It is not used after initial setup
        class ServiceConfiguration
        {

            public uint CalculateStatisticsInterval { get; set; }
            public uint WriteCacheToFileInterval { get; set; }
            public uint DataRecordingInterval { get; set; }
            public uint WhitelistCheckInterval { get; set; }
            public uint ConfigCheckInterval { get; set; }

            public ServiceConfiguration(uint logInt, uint cacheInt, uint dataInt, uint whitelistInt, uint configInt)
            {
                CalculateStatisticsInterval = logInt;
                WriteCacheToFileInterval = cacheInt;
                DataRecordingInterval = dataInt;
                WhitelistCheckInterval = whitelistInt;
                ConfigCheckInterval = configInt;
            }
        }
    }
}