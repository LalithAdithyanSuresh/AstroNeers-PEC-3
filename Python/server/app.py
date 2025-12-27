from flask import Flask, render_template, request, redirect, jsonify
import os
import subprocess
import glob
import json

app = Flask(__name__)

# --- PATH CONFIGURATION ---
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
# Go up one level from 'server' to 'Python' (Project Root)
PYTHON_DIR = os.path.join(BASE_DIR, '..')
# Assuming standard Unity structure where Assets is in the project root
ASSETS_DIR = os.path.join(PYTHON_DIR, 'Assets')
CONFIG_PATH = os.path.join(PYTHON_DIR, 'moon_rover_config.yaml')
RESULTS_DIR = os.path.join(PYTHON_DIR, 'results')

@app.route('/')
def index():
    # 1. Read the YAML file as raw text to preserve comments
    if os.path.exists(CONFIG_PATH):
        with open(CONFIG_PATH, 'r') as f:
            yaml_content = f.read()
    else:
        yaml_content = "# Config file not found!"

    # 2. Scan 'results' folder for existing Run IDs
    existing_runs = []
    if os.path.exists(RESULTS_DIR):
        # List directories only
        existing_runs = [d for d in os.listdir(RESULTS_DIR) 
                         if os.path.isdir(os.path.join(RESULTS_DIR, d))]
    
    return render_template('index.html', yaml_content=yaml_content, runs=existing_runs)

@app.route('/save_yaml', methods=['POST'])
def save_yaml():
    # Save the raw text from the editor directly to the file
    new_content = request.form['yaml_code']
    with open(CONFIG_PATH, 'w') as f:
        f.write(new_content)
    return jsonify({"status": "success", "message": "Configuration Saved!"})

@app.route('/start_training', methods=['POST'])
def start_training():
    run_id = request.form.get('run_id')
    mode = request.form.get('mode') # 'new' or 'resume'
    
    # Construct command
    # We must cd to 'Python' dir first so mlagents finds the yaml and results folder correctly
    cmd = f'start cmd /k "title ML-AGENTS: {run_id} && cd .. && mlagents-learn moon_rover_config.yaml --run-id={run_id}'
    
    if mode == 'resume':
        cmd += ' --resume'
    else:
        # If new, we might want --force to overwrite if it exists, or just default
        cmd += ' --force' 
    
    cmd += '"' # Close the cmd quote
    
    os.system(cmd)
    return jsonify({"status": "success", "message": f"Training {mode.upper()} started for {run_id}"})

@app.route('/launch_tensorboard')
def launch_tensorboard():
    # Opens TensorBoard in a persistent terminal window pointing to 'results'
    # We navigate to 'Python' folder (..) then run tensorboard on 'results'
    cmd = 'start cmd /k "title TENSORBOARD && cd .. && tensorboard --logdir results --port 6006"'
    os.system(cmd)
    return jsonify({"status": "success", "message": "TensorBoard Launching..."})

# --- NEW MAP VISUALIZATION ROUTES ---

@app.route('/view_map')
def view_map():
    # Serves the NEW 3D viewer template
    return render_template('map_viewer.html')

@app.route('/get_latest_map')
def get_latest_map():
    # Pattern to match the files exported by Unity
    search_pattern = os.path.join(ASSETS_DIR, "RoverMap_Gen*.json")
    list_of_files = glob.glob(search_pattern)
    
    if not list_of_files:
        return jsonify({"error": "No map files found in Assets directory."}), 404
        
    # Get the latest file based on modification time
    latest_file = max(list_of_files, key=os.path.getctime)
    
    try:
        with open(latest_file, 'r') as f:
            data = json.load(f)
        # Add metadata about the file
        data['filename'] = os.path.basename(latest_file)
        return jsonify(data)
    except Exception as e:
        return jsonify({"error": str(e)}), 500

if __name__ == '__main__':
    app.run(debug=True, port=5000)