using System;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;
using OpenCvSharp;
using Rect = OpenCvSharp.Rect;
using OpenCvSharp.Demo;

public static class ImageProcessing
{
    private static Mat Texture2Mat(Texture2D image)
    {
        Color32[] pixels = image.GetPixels32();
        Mat mat = new Mat(image.height, image.width, MatType.CV_8UC3);

        unsafe
        {
            fixed (Color32* ptr = pixels)
            {
                using (Mat temp = new Mat(image.height, image.width, MatType.CV_8UC4, (IntPtr)ptr))
                {
                    Cv2.CvtColor(temp, mat, ColorConversionCodes.RGBA2BGR);
                }
            }
        }
        
        return mat;
    }


    public static void MatFilter(Texture2D image, string filePath)
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
        Mat redMask = mask2 | mask1;
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


        // FOR DEBUG - Converting the output Texture2D into a PNG
        byte[] bytes = outputTex.EncodeToPNG();
        string path = Path.Combine(filePath, "CameraViewAfter.png");
        File.WriteAllBytes(path, bytes);
    }


    public static void CannyMethod(Texture2D image, string filePath)
    {
        Mat mat = Texture2Mat(image);
        Mat grayScaleMat = new Mat();
        Cv2.CvtColor(mat, grayScaleMat, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(grayScaleMat, grayScaleMat, new Size(5, 5), 1.5, 1.5);


        // FOR DEBUG - gray into PNG file
        // Convert grayscale (1 channel) to RGB so Unity can handle it
        // IMPORTANT: CRASHES WITHOUT THIS BLOCK
        Mat grayColor = new Mat();
        Cv2.CvtColor(grayScaleMat, grayColor, ColorConversionCodes.GRAY2RGB);

        // Convert to Texture2D
        Texture2D grayOutput = new Texture2D(grayColor.Cols, grayColor.Rows, TextureFormat.RGB24, false);
        grayOutput.LoadRawTextureData(grayColor.Data, grayColor.Cols * grayColor.Rows * 3);
        grayOutput.Apply();

        // Encode to PNG and save
        byte[] grayBytes = grayOutput.EncodeToPNG();
        string grayPath = Path.Combine(filePath, "CameraViewBeforeCanny.png");
        File.WriteAllBytes(grayPath, grayBytes);



        Mat edges = new Mat();
        Cv2.Canny(grayScaleMat, edges, 30, 50);



        // FOR DEBUG - Converting the single-channel edge map to RGB then to PNG
        Mat edgesColor = new Mat();
        Cv2.CvtColor(edges, edgesColor, ColorConversionCodes.GRAY2RGB);
        Texture2D output = new Texture2D(edgesColor.Cols, edgesColor.Rows, TextureFormat.RGB24, false);
        output.LoadRawTextureData(edgesColor.Data, edgesColor.Cols * edgesColor.Rows * 3);
        byte[] bytes = output.EncodeToPNG();
        string path = Path.Combine(filePath, "CameraViewAfter.png");
        File.WriteAllBytes(path, bytes);
    }
}
