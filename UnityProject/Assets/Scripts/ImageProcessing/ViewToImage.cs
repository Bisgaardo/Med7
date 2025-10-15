using System.Diagnostics;
using System.IO;
using Unity.VisualScripting;
using UnityEngine;
using File = UnityEngine.Windows.File;
using Input = UnityEngine.Input;

public class ViewToImage : MonoBehaviour
{
    [SerializeField] private Camera _camera;
    public Texture2D CameraViewImage;
    [SerializeField] private int width = 1920;
    [SerializeField] private int height = 1080;
    [SerializeField] private string path = "C:/Users/katri/Pictures/Work";
    private Stopwatch stopwatch = new Stopwatch();
    private string relativePythonPath = "Scripts/ImageProcessing/SAM.py";

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        _camera.transform.position = new Vector3(Camera.main.transform.position.x, Camera.main.transform.position.y, Camera.main.transform.position.z);

        // SPACE - Screenshot and run Canny Edge Detection
        if (Input.GetKeyDown(KeyCode.Space))
        {
            // FOR DEBUG - Stopwatch to measure the time (in ms)
            //Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();


            // Convert to Texture2D and PNG
            CameraViewToImage();


            stopwatch.Stop();
            print($"Screenshot took: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Restart();


            // Image processing function
            ImageProcessing.CannyMethod(CameraViewImage, path);


            stopwatch.Stop();
            print($"Processing took: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
        }

        // K - Take screenshot to use in SAM
        if (Input.GetKeyDown(KeyCode.K))
        {
            // FOR DEBUG - Stopwatch to measure the time (in ms)
            //Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();


            // Convert to Texture2D and PNG
            CameraViewToImage();


            stopwatch.Stop();
            print($"Screenshot took: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Restart();


            // Run python file
            PyRunner.Run(relativePythonPath);


            stopwatch.Stop();
            print($"Processing took: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
        }
    }

    private void CameraViewToImage()
    {
        // Create a temporary RenderTexture
        RenderTexture rt = new RenderTexture(width, height, 24);
        _camera.targetTexture = rt;

        // Render the camera’s view
        _camera.Render();

        // Activate RenderTexture and read pixels into Texture2D
        RenderTexture.active = rt;
        CameraViewImage = new Texture2D(width, height, TextureFormat.RGB24, false);
        CameraViewImage.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        CameraViewImage.Apply();

        // Reset
        _camera.targetTexture = null;
        RenderTexture.active = null;
        Destroy(rt);


        // FOR DEBUG - Convert the Texture2D to a PNG with a set path
        byte[] bytes = CameraViewImage.EncodeToPNG();
        string newPath = Path.Combine(path, "CameraView.png");
        File.WriteAllBytes(newPath, bytes);
    }
}
