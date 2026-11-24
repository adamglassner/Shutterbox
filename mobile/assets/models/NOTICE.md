# Models used by Shutterbox

All three run on your own device; nothing is uploaded. From [OpenCV Zoo](https://github.com/opencv/opencv_zoo) at commit 48b10fa505aeb51fbd127fc60a5c660040e823b7.

| File | What it does | Licence |
|---|---|---|
| `face_detection_yunet_2023mar.onnx` | Finds faces (YuNet, Shiqi Yu et al.) | MIT, Copyright (c) 2020 Shiqi Yu |
| `face_recognition_sface_2021dec_int8.onnx` | Tells faces apart (SFace, Zhong et al.) | Apache License 2.0 |
| `image_classification_mobilenetv2_2022apr_int8.onnx` | Recognises 1,000 kinds of things (MobileNetV2, ImageNet) | Apache License 2.0 |

`vision_words.txt` is made by `tools/make_vision_words.py` from the ImageNet class names.

## MIT License (YuNet)

Copyright (c) 2020 Shiqi Yu <shiqi.yu@gmail.com>

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Apache License 2.0 (SFace, MobileNetV2)

Licensed under the Apache License, Version 2.0 (the "License"); you may not use these files except in compliance with the License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.
