
import numpy as np
import torch
import matplotlib.pyplot as plt
import cv2
from segment_anything import sam_model_registry, SamPredictor


sam = sam_model_registry["default"](checkpoint=r"C:\Users\katri\University\Semester 1\Projekt\Files\sam_vit_h_4b8939.pth")
predictor = SamPredictor(sam)

image = cv2.imread(r"C:\Users\katri\Pictures\Work\Truck.png")
image = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)

predictor.set_image(image)
masks, _, _ = predictor.predict()


