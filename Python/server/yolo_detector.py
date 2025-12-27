from flask import Flask, render_template, Response, request, jsonify
import cv2
import numpy as np
import threading
import time
import random
from ultralytics import YOLO

app = Flask(__name__)

# --- CONFIGURATION ---
PORT = 8000  # <--- CHANGED TO 8000 (Safe & Standard)
MODEL_PATH = 'best.pt'
CONFIDENCE_THRESHOLD = 0.5
MAX_CAMERAS = 5

# Global Storage
# latest_frames: Stores the processed JPEG bytes for web streaming
latest_frames = {i: None for i in range(MAX_CAMERAS)}
# camera_locks: Prevents processing build-up (drops frames if busy)
camera_locks = {i: False for i in range(MAX_CAMERAS)}
# rock_counter: A set to track unique rock IDs generated (simulating "different" rocks)
unique_rock_ids = set()

# Load YOLO Model
print("⏳ Loading YOLO Model...")
try:
    model = YOLO(MODEL_PATH)
    print("✅ Model Loaded.")
except Exception as e:
    print(f"❌ Error loading model: {e}")
    print("   Ensure 'best.pt' is in the same folder!")
    exit()

def draw_boxes(image, detections):
    """Draws bounding boxes and data on the frame."""
    for det in detections:
        x, y, w, h = det['box']
        rock_id = det['rock_id']
        conf = det['confidence']
        
        # Draw Box (Purple/Green Theme)
        # BGR Color: (255, 0, 255) is Purple/Magenta
        cv2.rectangle(image, (x, y), (x+w, y+h), (255, 0, 255), 2)
        
        # Draw Label with background
        label = f"Rock #{rock_id}"
        (w_text, h_text), _ = cv2.getTextSize(label, cv2.FONT_HERSHEY_SIMPLEX, 0.6, 2)
        cv2.rectangle(image, (x, y - 20), (x + w_text, y), (255, 0, 255), -1)
        cv2.putText(image, label, (x, y - 5), 
                   cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2)
    return image

@app.route('/')
def index():
    """Renders the Dashboard Website."""
    return render_template('yolo_detector.html')

@app.route('/detect', methods=['POST'])
def detect():
    """Endpoint for Unity to send images."""
    try:
        camera_id = int(request.form.get('camera_id'))
        
        # Frame Dropping Logic
        if camera_locks.get(camera_id, False):
            return jsonify({"status": "dropped", "message": "Busy"}), 429

        camera_locks[camera_id] = True
        
        # Read Image
        file = request.files['file']
        nparr = np.frombuffer(file.read(), np.uint8)
        img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)

        if img is None:
            camera_locks[camera_id] = False
            return jsonify({"status": "error"}), 400

        # Run YOLO
        results = model.predict(img, conf=CONFIDENCE_THRESHOLD, verbose=False)
        detections = []

        for result in results:
            for box in result.boxes:
                # Generate a simulated unique ID (In a real scenario, use tracking)
                # We use a random ID here as per previous request logic
                rock_id = random.randint(1000, 9999)
                unique_rock_ids.add(rock_id)
                
                x, y, w, h = box.xywh[0].tolist()
                detections.append({
                    "rock_id": rock_id,
                    "confidence": round(float(box.conf[0]), 2),
                    "box": [int(x - w/2), int(y - h/2), int(w), int(h)]
                })

        # Process Image for Web Stream
        annotated_img = draw_boxes(img, detections)
        _, buffer = cv2.imencode('.jpg', annotated_img)
        latest_frames[camera_id] = buffer.tobytes()

        camera_locks[camera_id] = False
        
        return jsonify({
            "status": "success", 
            "rocks_found": len(detections)
        })

    except Exception as e:
        if 'camera_id' in locals():
            camera_locks[camera_id] = False
        print(f"Error: {e}")
        return jsonify({"status": "error", "message": str(e)}), 500

def generate_frames(camera_id):
    """Generator function for streaming video to HTML."""
    while True:
        frame_bytes = latest_frames.get(camera_id)
        if frame_bytes:
            yield (b'--frame\r\n'
                   b'Content-Type: image/jpeg\r\n\r\n' + frame_bytes + b'\r\n')
        else:
            # If no frame yet, yield a placeholder (black image)
            # This prevents the browser connection from timing out
            blank = np.zeros((480, 640, 3), np.uint8)
            cv2.putText(blank, "Waiting for Stream...", (50, 240), cv2.FONT_HERSHEY_SIMPLEX, 1, (255, 255, 255), 2)
            _, buffer = cv2.imencode('.jpg', blank)
            yield (b'--frame\r\n'
                   b'Content-Type: image/jpeg\r\n\r\n' + buffer.tobytes() + b'\r\n')
            time.sleep(1) # Slow retry if disconnected
        
        time.sleep(0.05) # Limit stream FPS to save CPU

@app.route('/video_feed/<int:camera_id>')
def video_feed(camera_id):
    """Route for the <img> tag source."""
    return Response(generate_frames(camera_id),
                    mimetype='multipart/x-mixed-replace; boundary=frame')

@app.route('/stats')
def stats():
    """API for the frontend to fetch the rock count."""
    return jsonify({"total_rocks": len(unique_rock_ids)})

if __name__ == '__main__':
    # Threaded=True is important for handling multiple camera streams + web requests
    print(f"🚀 Dashboard running at http://localhost:{PORT}")
    app.run(host='0.0.0.0', port=PORT, threaded=True)