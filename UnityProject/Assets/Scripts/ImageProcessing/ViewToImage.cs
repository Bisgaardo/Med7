using System.Diagnostics;
using System.IO;
using UnityEngine;
using File = UnityEngine.Windows.File;
using Input = UnityEngine.Input;

public class ViewToImage : MonoBehaviour
{
    [SerializeField] private Camera _camera;
    public Texture2D CameraViewImage;
    [SerializeField] private int width = 1920;
    [SerializeField] private int height = 1080;
    [SerializeField] private string path = "C:/Users/byacr/Pictures";
    private float startTime;
    private float endTime;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
    }

    // Update is called once per frame
    void Update()
    {
        _camera.transform.position = new Vector3(Camera.main.transform.position.x, Camera.main.transform.position.y, Camera.main.transform.position.z);
        if (Input.GetKeyDown(KeyCode.Space))
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            CameraViewToImage();
            stopwatch.Stop();
            print($"Screenshot took: {stopwatch.ElapsedMilliseconds} ms");
            stopwatch.Reset();
            stopwatch.Start();
            ImageProcessing.MatFilter(CameraViewImage);
            stopwatch.Stop();
            print($"Processing took: {stopwatch.ElapsedMilliseconds} ms");
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
        
        byte[] bytes = CameraViewImage.EncodeToPNG();
        string newPath = Path.Combine(path, "CameraView.png");
        File.WriteAllBytes(newPath, bytes);
    }
}
