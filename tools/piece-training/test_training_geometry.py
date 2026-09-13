"""Checks that crop/rotation augmentation keeps labels attached to actual pixels."""
import random
import unittest

try:
    import cv2
    import numpy as np
    from train_piece_detector import TrainingTiles, tile_starts, ownership_bounds, score_predictions
except ModuleNotFoundError as error:
    raise unittest.SkipTest("Training geometry checks require requirements-training.txt and CUDA PyTorch environment") from error


class TrainingGeometryTests(unittest.TestCase):
    def test_augmented_boxes_still_cover_their_visible_piece(self):
        pixels = np.zeros((1200,1920,3),dtype=np.uint8)
        pixels[240:280,440:510] = 255
        board = {"pixels":pixels,"boxes":np.array([[440,240,510,280,0]],dtype=np.float32)}
        dataset = TrainingTiles([board],100)
        random.seed(17)
        visible = 0
        for index in range(100):
            tensor, targets = dataset[index]
            # The synthetic piece stays brighter than its background under every gain/offset.
            mask = (tensor.numpy().max(0)>100).astype(np.uint8)
            locations = cv2.findNonZero(mask)
            labels = targets.numpy()[targets.numpy()[:,3]>0]
            if locations is None:
                self.assertEqual(len(labels),0)
                continue
            x,y,w,h = cv2.boundingRect(locations)
            # A <3px sliver may intentionally have no label.
            if not len(labels):
                self.assertLess(min(w,h),5)
                continue
            self.assertEqual(len(labels),1)
            _,cx,cy,bw,bh = labels[0]
            expected = np.array([cx-bw/2,cy-bh/2,cx+bw/2,cy+bh/2])
            np.testing.assert_allclose(expected,[x,y,x+w,y+h],atol=2)
            visible += 1
        self.assertGreater(visible,70)

    def test_overlap_ownership_covers_each_board_point_once(self):
        for length,expected in ((1920,[0,512,1024,1280]),(1200,[0,512,560])):
            starts = tile_starts(length)
            self.assertEqual(starts,expected)
            ownership = [ownership_bounds(starts,i,length) for i in range(len(starts))]
            for point in np.arange(.5,length,1):
                self.assertEqual(sum(low<=point<high for low,high in ownership),1)

    def test_duplicate_predictions_do_not_count_as_extra_matches(self):
        boxes = np.array([[10,10,30,20,0],[30,10,50,20,0]],dtype=np.float32)
        board = {"boxes":boxes,"item":{"fileName":"fixture.png","group":"held-out","boxes":[{"kind":"train","color":"black"}]*2}}
        predictions = [np.array([[10,10,30,20,.9,0],[10,10,30,20,.8,0],[30,10,50,20,.7,0]],dtype=np.float32)]
        result = score_predictions([board],predictions,.3)
        self.assertEqual((result["summary"]["tp"],result["summary"]["fp"],result["summary"]["fn"]),(2,1,0))


if __name__=="__main__":
    unittest.main()
