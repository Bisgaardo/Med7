using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
public static class PyRunner
{
    private static readonly string pythonExePath = @"C:\Users\katri\Documents\GitHub\Med7\venv\Scripts\python.exe";
    private static Image image;
    // Run Python files inside Unity's assets folder
    public static string Run(string relativePythonPath)
    {
        // Creating an absolute path
        string pythonFileFullPath = Path.Combine(Application.dataPath, relativePythonPath);
        string outputPath = null;

        //Check if the python file exists
        if (!System.IO.File.Exists(pythonFileFullPath))
        {
            UnityEngine.Debug.LogError($"Python file not found: {pythonFileFullPath}");
            return null;
        }

        // Setup for running python
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            // Python version
            FileName = pythonExePath,

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

            // Extract the file path from Python output
            outputPath = output.Trim();
        }
        return outputPath;
    }
}