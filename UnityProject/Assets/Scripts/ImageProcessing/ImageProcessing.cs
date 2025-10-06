using System;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;
using OpenCvSharp;
using Rect = OpenCvSharp.Rect;

public static class ImageProcessing
{
    
    public static void MatFilter(Texture2D image)
    {
        Color32[] colors = image.GetPixels32();
        Mat mat = new Mat(image.height, image.width, MatType.CV_8UC3);
        Mat hsv = new Mat();
        Mat rgb = new Mat();

        unsafe
        {
            fixed (Color32* ptr = colors)
            {
                using (Mat temp = new Mat(image.height, image.width, MatType.CV_8UC4, (IntPtr)ptr))
                {
                    Cv2.CvtColor(temp, mat, ColorConversionCodes.RGBA2BGR);
                }
            }
        }
        Cv2.CvtColor(mat, hsv, ColorConversionCodes.BGR2HSV);
        
        // Define HSV range for red
        Scalar lowerRed1 = new Scalar(0, 100, 100);
        Scalar upperRed1 = new Scalar(20, 255, 255);
        Scalar lowerRed2 = new Scalar(170, 100, 100);
        Scalar upperRed2 = new Scalar(180, 255, 255);
        Scalar lowerCyan = new Scalar(80, 100, 100);
        Scalar upperCyan = new Scalar(100, 255, 255);
        
        // Create masks
        Mat mask1 = new Mat();
        Mat mask2 = new Mat();
        Mat mask3 = new Mat();
        Cv2.InRange(hsv, lowerRed1, upperRed1, mask1);
        Cv2.InRange(hsv, lowerCyan, upperCyan, mask3);
        Cv2.InRange(hsv, lowerRed2, upperRed2, mask2);
        
        // Combine masks
        Mat redMask =  mask2 | mask1;
        Mat mask = redMask | mask3;
        
        // Find contours
        Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        
        // Draw bounding boxes
        foreach (var contour in contours)
        {
            Rect rect = Cv2.BoundingRect(contour);
            Cv2.Rectangle(mat, rect, new Scalar(0, 255, 0), 2);
        }
        
        Cv2.CvtColor(mat, rgb, ColorConversionCodes.BGR2RGB);

        Texture2D outputTex = new Texture2D(rgb.Cols, rgb.Rows, TextureFormat.RGB24, false);
        outputTex.LoadRawTextureData(rgb.Data, rgb.Cols * rgb.Rows * 3);
        outputTex.Apply();
        
        byte[] bytes = outputTex.EncodeToPNG();
        string path = Path.Combine("C:/Users/byacr/Pictures", "CameraViewAfter.png");
        File.WriteAllBytes(path, bytes); 
    }
}
