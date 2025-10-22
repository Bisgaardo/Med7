using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using System.Threading.Tasks;


public class ViewToImage : MonoBehaviour
{
    public Texture2D CameraViewImage;
    [SerializeField] private Camera _camera;
    [SerializeField] private int width = 1920;
    [SerializeField] private int height = 1080;
    [SerializeField] private string path = "C:/Users/katri/Pictures/Work";
    [SerializeField] private RawImage samImage;
    [SerializeField] private GameObject spinner;
    private Stopwatch stopwatch = new Stopwatch();
    private string absolutePythonPath = "C:/Users/katri/Documents/GitHub/Med7/src/SAM.py";

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        spinner.SetActive(false);
        samImage.gameObject.SetActive(false);

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
            UnityEngine.Debug.Log("Starting process...");
            samImage.gameObject.SetActive(false);

            // Run python file
            RunSAM();
        }
    }
    
    private async void RunSAM()
    {
        spinner.SetActive(true);

        stopwatch.Start();

        // Run Python in background thread
        string outputPath = await Task.Run(() => PyRunner.Run(absolutePythonPath));

        stopwatch.Stop();
        spinner.SetActive(false);

        // Back on main thread: create Texture2D
        if (!string.IsNullOrEmpty(outputPath) && System.IO.File.Exists(outputPath))
        {
            byte[] bytes = System.IO.File.ReadAllBytes(outputPath);
            Texture2D pyImage = new Texture2D(2, 2);
            pyImage.LoadImage(bytes);

            samImage.texture = pyImage;
            samImage.gameObject.SetActive(true);
        }
        else
        {
            UnityEngine.Debug.LogError("Invalid file from Python");
        }

        UnityEngine.Debug.Log($"Processing took: {stopwatch.ElapsedMilliseconds / 1000f} seconds");
        stopwatch.Reset();
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
