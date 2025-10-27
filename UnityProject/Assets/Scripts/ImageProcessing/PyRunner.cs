using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
public static class PyRunner
{
    private static readonly string pythonExePath = @"C:\Users\katri\Documents\GitHub\Med7\venv\Scripts\python.exe";
    private static Image image;
    public static string Run(string relativePythonPath)
    {
        // Create an absolute path
        string pythonFileFullPath = Path.Combine(Application.dataPath, relativePythonPath);
        string outputPath = null;

        //Check if python file exists
        if (!System.IO.File.Exists(pythonFileFullPath))
        {
            UnityEngine.Debug.LogError($"Python file not found: {pythonFileFullPath}");
            return null;
        }

        // Setup info for running python
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            // Python version / Python.exe file
            FileName = pythonExePath,

            // Path for python file
            Arguments = $"\"{pythonFileFullPath}\"",

            // Don't use cmd/shell for exectuing
            UseShellExecute = false,

            // Output prints in Unity
            RedirectStandardOutput = true,

            // Error prints in Unity
            RedirectStandardError = true,

            // No console popup during execution
            CreateNoWindow = true
        };

        // Run the process
        using (Process process = new Process())
        {
            // Use the setup information for this process and run
            process.StartInfo = startInfo;
            process.Start();

            // Read and save outputs & errors from python
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            // Freeze Unity until python is done
            process.WaitForExit();

            // Log python outputs and errors in Unity
            UnityEngine.Debug.Log($"[Python Output]\n{output}");
            if (!string.IsNullOrEmpty(error))
                UnityEngine.Debug.LogError($"[Python Error]\n{error}");

            // Extract the file path from Python output
            outputPath = output.Trim();
        }
        return outputPath;
    }
}