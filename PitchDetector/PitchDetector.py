import numpy as np
import pyaudio
import threading
import atexit
import scipy.signal as signal
import time
from note_frequencies import notes

windowSize = 1024
samplingRate = 5000

class MicrophoneRecorder:
    def __init__(self, r=samplingRate, w=windowSize):      # good for piano: 5000, 1024.... for violin 20000,1024
        self.rate = r
        self.window = w
        self.frames = []
        self.p = pyaudio.PyAudio()
        self.lock = threading.Lock()
        self.cv = threading.Condition(self.lock)

        self.stream = self.p.open(format=pyaudio.paInt16, channels=1, rate=self.rate, input=True, frames_per_buffer=self.window, stream_callback=self.add_frame)

        atexit.register(self.close_stream)

    def add_frame(self, data, frame_count, time_info, status):
        data = np.fromstring(data, 'int16')
        self.cv.acquire()

        self.frames.append(data)

        self.cv.notify()
        self.cv.release()

        return None, pyaudio.paContinue

    def get_frames(self):
        self.cv.acquire()

        while len(self.frames) == 0:
            self.cv.wait()

        frames = self.frames
        self.frames = []
        self.cv.release()

        return frames

    def start_recording(self):
        self.stream.start_stream()

    def close_stream(self):
        self.stream.close()
        self.p.terminate()

class PitchDetector:
    def __init__(self):

        self.init()

        self._thread = threading.Thread(target = self.add_data)
        self._thread.start()

    def init(self):
        mic = MicrophoneRecorder()
        mic.start_recording()  
       
        self.mic = mic

        self.freqs = np.fft.rfftfreq(mic.window, 1./mic.rate)
        self.time = np.arange(mic.window, dtype=np.float32) / mic.rate * 1000

    def add_data(self):
        while True:
            frames = self.mic.get_frames()

            if len(frames) > 0:
                cur_frame = frames[-1]

                B, A = signal.butter(3, 0.6, output='ba')
                cur_frame = signal.filtfilt(B,A, cur_frame)

                window = signal.gaussian(windowSize, std=0.15*windowSize)
                cur_frame = cur_frame * window
            
                # computes real valued signal
                fft_frame = np.fft.rfft(cur_frame)

                #calculate autocorrelation
                autocor = fft_frame * np.conj(fft_frame)
                result = np.fft.irfft(autocor)

                max_val = 1.5
                incremented = (-(max_val-1)/(self.time[windowSize - 1] - self.time[10]) * (self.time - self.time) + max_val) * abs(result) * 1./abs(result[0])
                incremented[len(incremented) / 2:] = [0] * (len(incremented) / 2)

                # remove the first values to avoid max issues
                incremented[:30] = [0] * 30
            
                maximum = np.argmax(abs(result[10:])) + 10
                time_max = round(self.time[maximum], 3)
                freq_max = 1000/time_max;
            
                if freq_max > 50:
                    p3 = [1000/self.time[maximum - 1], result[maximum - 1]]
                    p2 = [1000/self.time[maximum    ], result[maximum    ]]
                    p1 = [1000/self.time[maximum + 1], result[maximum + 1]]

                    interpol_max = self.quadratic_maximum(p1, p2, p3)
                
                    print 'maximum at: ', freq_max, ' Hz, Note: ', self.getNoteFromFrequency(freq_max)
                    print 'interpol max: ', interpol_max, ' Hz, Note: ', self.getNoteFromFrequency(interpol_max)

    def getNoteFromFrequency(self, freq):
        temp_notes = {k: abs(v - freq) for k, v in notes.items()}
        return min(temp_notes, key=temp_notes.get)

    def quadratic_maximum(self, p1, p2, p3):       
        _a = p1[1]/((p1[0]-p2[0])*(p1[0]-p3[0])) + p2[1]/((p2[0]-p1[0])*(p2[0]-p3[0])) + p3[1]/((p3[0]-p1[0])*(p3[0]-p2[0]))
        if _a == 0:
            return 0
        _b = -p1[1]*(p2[0] + p3[0])/((p1[0]-p2[0])*(p1[0]-p3[0])) - p2[1]*(p3[0] + p1[0])/((p2[0]-p1[0])*(p2[0]-p3[0])) - p3[1]*(p1[0] + p2[0])/((p3[0]-p1[0])*(p3[0]-p2[0]))
        _c = p1[1]*p2[0]*p3[0]/((p1[0]-p2[0])*(p1[0]-p3[0])) + p2[1]*p3[0]*p1[0]/((p2[0]-p1[0])*(p2[0]-p3[0])) + p3[1]*p1[0]*p2[0]/((p3[0]-p1[0])*(p3[0]-p2[0]))
        max_val = -_b/(2*_a)
        return max_val#_a * max_val**2 + _b * max_val + _c

window = PitchDetector()
while True:
    pass
