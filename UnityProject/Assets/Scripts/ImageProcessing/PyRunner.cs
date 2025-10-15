using System.Diagnostics;
using System.IO;
using UnityEngine;
using File = UnityEngine.Windows.File;
public static class PyRunner
{
    // Run Python files inside Unity's assets folder
    public static void Run(string relativePythonPath)
    {
        // Creating an absolute path
        string pythonFileFullPath = Path.Combine(Application.dataPath, relativePythonPath);

        //Check if the python file exists
        if (!File.Exists(pythonFileFullPath))
        {
            UnityEngine.Debug.LogError($"Python file not found: {pythonFileFullPath}");
            return;
        }

        // Setup for running python
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            // Python version
            FileName = "python3",

            // Path for python file
            Arguments = $"\"{pythonFileFullPath}\"",

            // Don't use cmd/shell for exectuing
            UseShellExecute = false,

            // Outputs print in Unity
            RedirectStandardOutput = true,

            // Errors print in Unity
            RedirectStandardError = true,

            // No console pops up during execution
            CreateNoWindow = true
        };

        // Run the process
        using (Process process = new Process())
        {
            // Use the "startInfo" information for this process
            process.StartInfo = startInfo;

            // Launch python interpreter
            process.Start();

            // Read and save outputs & errors from python
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            // Freeze Unity until python is done
            process.WaitForExit();

            // Log python outputs in the Unity console
            UnityEngine.Debug.Log($"[Python Output]\n{output}");

            // Log python errors in the Unity console
            if (!string.IsNullOrEmpty(error))
                UnityEngine.Debug.LogError($"[Python Error]\n{error}");
        }
    }
}