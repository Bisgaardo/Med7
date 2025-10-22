
import numpy as np
import cv2
import sys
import os
from segment_anything import sam_model_registry, SamPredictor


# Paths to checkpoint model and image
samCheckpointPath = r"C:\Users\katri\University\Semester 1\Projekt\Files\sam_vit_h_4b8939.pth"
imagePath = r"C:\Users\katri\Pictures\Work\Truck.png"

# Important variables
modelType = "vit_h"
device = "cuda"
sys.path.append("..")

# Coordinate location of cursor
pointValueX, pointValueY = 1300, 280


image = cv2.imread(imagePath)
image = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)

sam = sam_model_registry[modelType](checkpoint=samCheckpointPath)
sam.to(device=device)

predictor = SamPredictor(sam)
predictor.set_image(image)

# Position of the cursor and type (1 for positive, 0 for negative)
input_point = np.array([[pointValueX, pointValueY]])
input_label = np.array([1])


masks, scores, logits = predictor.predict(
    point_coords=input_point,
    point_labels=input_label,
    multimask_output=True,
)

# Pick highest score mask
best_mask_index = np.argmax(scores)
best_mask = masks[best_mask_index]

# Create overlay for the best mask
overlay = image.copy()
overlay[best_mask > 0] = [255, 255, 0]
overlayedImage = cv2.addWeighted(image, 0.7, overlay, 0.3, 0)



# ABANDONED - FOR COMBINING THE MASKS
##############################################################################
# # Combine all masks into one overlay for visualization
# combined_mask = np.zeros_like(image, dtype=np.uint8)

# for mask in masks:
#     color = np.array([30, 144, 255], dtype=np.uint8)  # Blue-ish
#     mask_rgb = np.zeros_like(image, dtype=np.uint8)
#     mask_rgb[mask] = color
#     combined_mask = cv2.addWeighted(combined_mask, 1, mask_rgb, 0.5, 0)

# # Overlay mask onto original image
# overlayedImage = cv2.addWeighted(image, 1.0, combined_mask, 0.5, 0)
##############################################################################



# Save the result to disk (same directory as input image or a fixed path)
outputPath = os.path.join(os.path.dirname(imagePath), "outputMasked.png")
cv2.imwrite(outputPath, cv2.cvtColor(overlayedImage, cv2.COLOR_RGB2BGR))

# Print the path so Unity can read it
print(outputPath)
